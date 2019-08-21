using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using Krs.Ats.IBNet;
using Krs.Ats.IBNet.Contracts;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sdl.MultiSelectComboBox.API;
using Easy.MessageHub;
using AmiBroker.OrderManager;
using System.Reflection;
using FastMember;

namespace AmiBroker.Controllers
{
    public enum PriceAlignDirection
    {
        Ceiling = 0,
        Floor = 1
    }
    public class IBContract
    {
        public Contract Contract { get; set; }
        public decimal MinTick { get; set; }
        public double RoundLotSize { get; set; }
        public string TradingHours { get; set; }
        public int ReqId { get; set; }
        public string TimeZoneId { get; set; }
        public string Multiplier { get; set; }
        public static SecurityType SecTypeConverter(string secType)
        {
            SecurityType securityType = SecurityType.Stock;
            switch (secType.ToLower())
            {
                case "stk":
                    securityType = SecurityType.Stock;
                    break;
                case "cash":
                    securityType = SecurityType.Cash;
                    break;
                case "fut":
                    securityType = SecurityType.Future;
                    break;
                case "opt":
                    securityType = SecurityType.Option;
                    break;
                case "ind":
                    securityType = SecurityType.Index;
                    break;
                case "fop":
                    securityType = SecurityType.FutureOption;
                    break;
                case "bag":
                    securityType = SecurityType.Bag;
                    break;
                case "bond":
                    securityType = SecurityType.Bond;
                    break;
                case "war":
                    securityType = SecurityType.Warrant;
                    break;
                case "cmdty":
                    securityType = SecurityType.Commodity;
                    break;
                case "bill":
                    securityType = SecurityType.Bill;
                    break;
                default:
                    securityType = SecurityType.Undefined;
                    break;
            }
            return securityType;
        }
    }
    public class IBController : IController, INotifyPropertyChanged
    {
        private readonly object idLock = new object();
        //private readonly object ibLock = new object();
        // there is one instance for one account, so lock should not be static
        //private readonly AsyncLock m_lock = new AsyncLock();
        private int OrderIdCount = 0;
        
        private int _failedOrderId = -1;
        private int FailedOrderId {  get { return _failedOrderId--; } }

        readonly MessageHub _hub = MessageHub.Instance;
        private bool disconnectByManual = false;
        public Type Type { get { return this.GetType(); } }
        public IBClient Client { get; } = new IBClient();
        public static Dictionary<string, IBContract> Contracts { get; } = new Dictionary<string, IBContract>();
        public static Dictionary<int, Order> Orders { get; } = new Dictionary<int, Order>();

        public static Dictionary<int, CancelingOrder> CancelingOrders { get; } = new Dictionary<int, CancelingOrder>();

        private bool _pDummy;
        public bool Dummy
        {
            get { return _pDummy; }
            set
            {
                if (_pDummy != value)
                {
                    _pDummy = value;
                    OnPropertyChanged("Dummy");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<AccountInfo> Accounts { get; }
        public string Vendor { get; } = "IB";
        public string VendorFullName { get; } = "Interactive Broker";

        private ConnectionParam _pConnParam;
        public ConnectionParam ConnParam
        {
            get { return _pConnParam; }
            set
            {
                if (_pConnParam != value)
                {
                    _pConnParam = value;
                    DisplayName = Vendor + "(" + value.AccName + ")";
                    OnPropertyChanged("ConnParam");
                }
            }
        }

        private bool _pIsConnected = false;
        public bool IsConnected
        {
            get
            {
                return Client.Connected;
            }
            set
            {
                if (_pIsConnected != value)
                {
                    _pIsConnected = value;
                    if (value) timer.Stop();
                    OnPropertyChanged("IsConnected");
                }
            }
        }

        private string _pName;
        public string DisplayName
        {
            get { return _pName; }
            set
            {
                if (_pName != value)
                {
                    _pName = value;
                    OnPropertyChanged("DisplayName");
                }
            }
        }

        private BrokerConnectionStatus _pConnectionStatus = BrokerConnectionStatus.Disconnected;
        public BrokerConnectionStatus ConnectionStatus
        {
            get { return _pConnectionStatus; }
            private set
            {
                if (_pConnectionStatus != value)
                {
                    _pConnectionStatus = value;
                    OnPropertyChanged("ConnectionStatus");
                }
            }
        }

        private AccountInfo _pSelectedAccount;
        public AccountInfo SelectedAccount
        {
            get { return _pSelectedAccount; }
            set
            {
                if (_pSelectedAccount != value)
                {
                    _pSelectedAccount = value;
                    OnPropertyChanged("SelectedAccount");
                }
            }
        }


        public BitmapImage Image { get; }
        public Size ImageSize { get; }
        public IItemGroup Group { get; set; }
        public bool IsEnabled { get; set; } = true; // indicate if seletable in multiselect combox

        private string last_req_account;
        private Timer _timer = null;
        public MainViewModel MainVM { get; private set; }
        public IBController(MainViewModel vm)
        {
            MainVM = vm; //it's neccessary since constructor being called during MainViewModel constructing
            setHandler();
            Accounts = new ObservableCollection<AccountInfo>();
            Uri uri = new Uri("pack://application:,,,/OrderManager;component/Controllers/images/ib.png");
            Image = new BitmapImage(uri);
            ImageSize = new Size(16, 16);
            Group = DefaultGroupService.GetItemGroup("IB");
            //Accounts.CollectionChanged += Accounts_CollectionChanged;
        }

        private void Accounts_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (Accounts.Count > 1)
            {
                _timer = new Timer(x => RefreshPortfolio(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            }
            else
            {
                if (_timer != null)
                    _timer.Dispose();
            }
        }

        private void setHandler()
        {
            Client.NextValidId += eh_NextValidId; ;
            Client.ConnectionClosed += eh_ConnectionClosed;
            Client.Error += eh_Error;
            Client.OrderStatus += eh_OrderStatus;
            Client.OpenOrder += eh_OpenOrder;
            Client.ManagedAccounts += eh_ManagedAccounts;
            Client.Position += eh_Position;
            Client.UpdatePortfolio += eh_UpdatePortfolio;
            //EventDispatcher.AccountSummary += eh_AccountSummary;
            Client.UpdateAccountValue += eh_AccountValue;
            Client.ExecDetails += eh_ExecDetails;
            //Client.ConnectionStatus += eh_ConnectionStatus; 
        }

        private void eh_NextValidId(object sender, NextValidIdEventArgs e)
        {
            AfterConnected(e.OrderId);
        }

        public async Task<ContractDetailsEventArgs> test()
        {
            /*
            List<Contract> contracts = new List<Contract>();
            //EventDispatcher.ContractDetails += EventDispatcher_ContractDetails;
            contracts.Add(new Contract { LocalSymbol = "EUR.AUD", Exchange="IDEALPRO", SecType="CASH" });
            contracts.Add(new Contract { LocalSymbol = "MHIG9", Exchange = "HKFE", SecType = "FUT" });
            //contracts.Add(new Contract { LocalSymbol = "QQQ", Exchange = "SMART", SecType = "STK" });
            contracts.Add(new Contract { LocalSymbol = "5", Exchange = "SEHK", SecType = "STK" });
            //ClientSocket.reqContractDetails(0, contract);
            //return null;*/
            List<string> contracts = new List<string>();
            contracts.Add("EUR.AUD-IDEALPRO-CASH");
            try
            {
                foreach (var contract in contracts)
                {
                    var c = await reqContractDetailsAsync(contract);
                }
            }
            catch (Exception ex)
            {
                int i = 0;

            }

            return null;
        }
        public async Task<IBContract> reqContractDetailsAsync(string symbolName, bool refresh = false, int? timeout = null)
        {
            Contract contract = new Contract();
            string[] parts = symbolName.Split(new char[] { '-' });
            switch (parts.Length)
            {
                case 1:
                    contract.LocalSymbol = parts[0];
                    contract.Exchange = "SMART";
                    contract.SecurityType = SecurityType.Stock;
                    break;
                case 2:
                    contract.LocalSymbol = parts[0];
                    contract.Exchange = parts[1];
                    contract.SecurityType = SecurityType.Stock;
                    break;
                case 3:
                    contract.LocalSymbol = parts[0];
                    contract.Exchange = parts[1];
                    contract.SecurityType = IBContract.SecTypeConverter(parts[2]);
                    break;
                case 4:
                    contract.LocalSymbol = parts[0];
                    contract.Exchange = parts[1];
                    contract.SecurityType = IBContract.SecTypeConverter(parts[2]);
                    contract.Currency = parts[3];
                    break;
            }
            return await reqContractDetailsAsync(contract, refresh, timeout);
        }
        public async Task<IBContract> reqContractDetailsAsync(Contract contract, bool refresh = false, int? timeout = null)
        {
            string symbol = contract.LocalSymbol + "-" + contract.Exchange + "-"
                + contract.SecurityType.GetEnumDescription()
                + (contract.Currency != null ? "-" + contract.Currency : "");
            if (!refresh && Contracts.ContainsKey(symbol))
                return Contracts[symbol];

            try
            {
                IBContract c = await IBTaskExt.FromEvent<EventArgs, IBContract>(
                handler =>
                {
                    Client.ContractDetails += new EventHandler<ContractDetailsEventArgs>(handler);
                    //EventDispatcher.MarketRule += new EventHandler<MarketRuleEventArgs>(handler);
                    Client.Error += new EventHandler<ErrorEventArgs>(handler);
                },
                (reqId) => { Client.RequestContractDetails(reqId, contract); return (0, OrderStatus.None, OrderExecutionError.None); },
                handler =>
                {
                    Client.ContractDetails -= new EventHandler<ContractDetailsEventArgs>(handler);
                    //EventDispatcher.MarketRule -= new EventHandler<MarketRuleEventArgs>(handler);
                    Client.Error -= new EventHandler<ErrorEventArgs>(handler);
                },
                CancellationToken.None, this, timeout);
                if (!Contracts.ContainsKey(symbol))
                    Contracts.Add(symbol, c);
                return c;
            }
            catch (Exception ex)
            {
                //throw ex;
                if (!ex.Message.Contains("Request Contract Data Sending Error") &&
                    !ex.Message.Contains("A task was canceled"))
                    GlobalExceptionHandler.HandleException("IBController.reqContractDetailsAsync", ex);
                return null;
            }
        }
        public async Task<int> reqIdsAsync()
        {
            try
            {
                var c = await IBTaskExt.FromEventToAsync<NextValidIdEventArgs>(
                handler =>
                {
                    Client.NextValidId += new EventHandler<NextValidIdEventArgs>(handler);
                },
                () => Client.RequestIds(1),
                handler =>
                {
                    Client.NextValidId -= new EventHandler<NextValidIdEventArgs>(handler);
                },
                CancellationToken.None,
                () => Client.NextValidId -= eh_NextValidId,
                () => Client.NextValidId += eh_NextValidId);
                return c.OrderId;
            }
            catch (Exception ex)
            {
                //throw ex;
                GlobalExceptionHandler.HandleException("IBController.reqIdsAsync", ex);
                return -1;
            }
        }
        private void Request()
        {
            Client.RequestManagedAccts();
            Client.RequestAllOpenOrders();
            Client.RequestAutoOpenOrders(true);
            Client.RequestPositions();    // notify to swich reqAccountUpdates() to update portfolio
                                          //Client.r(1, "All", AccountSummaryTags.GetAllTags());
                                          //ClientSocket.reqAccountUpdates(); 
        }

        private void eh_Position(object sender, PositionEventArgs e)
        {
            // get portfolio data

            if (last_req_account != e.Account)
            {
                Client.RequestAccountUpdates(true, e.Account);
            }

        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="accountInfo"></param>
        /// <param name="orderType"></param>
        /// <param name="orderAction"></param>
        /// <param name="BarIndex"></param>
        /// <param name="posSize"></param>
        /// <returns></returns>       
        private Order TransformIBOrder(AccountInfo accountInfo, Strategy strategy, BaseOrderType orderType, OrderAction orderAction, out string message, out decimal orgLmtPrice, double? posSize = null)
        {
            message = string.Empty;
            orgLmtPrice = -1;
            Order order = new Order();
            IBOrderType ot = orderType as IBOrderType;
            SymbolInAction symbol = strategy?.Symbol;
            order.Transmit = ot.Transmit;

            if (posSize == null)
            {
                float ps = 0;
                if (!string.IsNullOrEmpty(orderType.PositionSize))
                    ps = (float)(strategy.CurrentPosSize[orderType.PositionSize]);
                else
                    ps = strategy.DefaultPositionSize;

                if (orderAction == OrderAction.Buy || orderAction == OrderAction.Short)
                {
                    order.TotalQuantity = (int)(ps * symbol.RoundLotSize);
                }
                else if (orderAction == OrderAction.Sell || orderAction == OrderAction.Cover)
                {
                    double pos = 0; // get existing size of open position
                    if (orderAction == OrderAction.Sell)
                        pos = strategy.AccountStat[accountInfo.Name].LongPosition;
                    else if (orderAction == OrderAction.Cover)
                        pos = strategy.AccountStat[accountInfo.Name].ShortPosition;

                    order.TotalQuantity = (int)(Math.Min(pos, ps) * symbol.RoundLotSize);

                    if (pos > ps)
                    {
                        MainVM.Log(new Log
                        {
                            Time = DateTime.Now,
                            Text = string.Format("Warning: existing position({0:0}) is greater than specified one(({1:0}).", pos, strategy.PositionSize),
                            Source = symbol.Name + "." + strategy.Name + "." + orderAction.ToString() + "." + accountInfo.Name
                        });
                    }
                }
                else if (orderAction == OrderAction.APSLong || orderAction == OrderAction.APSShort)
                {
                    double pos = 0; // get existing size of open position
                    if (orderAction == OrderAction.APSLong)
                        pos = strategy.AccountStat[accountInfo.Name].LongPosition;
                    else if (orderAction == OrderAction.APSShort)
                        pos = strategy.AccountStat[accountInfo.Name].ShortPosition;
                    order.TotalQuantity = (int)(pos * symbol.RoundLotSize);
                }
            }
            else if (posSize == -1)
            {
                // do nothing
                // multiple slippages need calculating
            }
            else
            {
                order.TotalQuantity = (int)posSize;
            }

            if (symbol != null && posSize != -1)
            {
                if (symbol.MinOrderSize > 0 && symbol.MaxOrderSize > 0 &&
                (order.TotalQuantity < symbol.MinOrderSize || order.TotalQuantity > symbol.MaxOrderSize))
                {
                    MainVM.Log(new Log
                    {
                        Time = DateTime.Now,
                        Text = string.Format("Total quantity {0:0} is out of range({1:0} - {2:0})", order.TotalQuantity, symbol.MinOrderSize, symbol.MaxOrderSize),
                        Source = symbol?.Name + "." + strategy?.Name + "." + accountInfo.Name
                    });
                }
            }

            order.Action = (orderAction == OrderAction.Buy || orderAction == OrderAction.Cover
                || orderAction == OrderAction.StoplossShort || orderAction == OrderAction.APSShort
                || orderAction == OrderAction.PreForceExitShort || orderAction == OrderAction.FinalForceExitShort) ? ActionSide.Buy : ActionSide.Sell;
            order.OrderType = ot.OrderType;
            order.Account = accountInfo.Name;
            order.GoodAfterTime = ot.GoodAfterTime.ToString();
            order.GoodTillDate = ot.GoodTilDate.ToString();
            if (order.GoodTillDate != null)
                order.Tif = TimeInForce.GoodTillDate;
            else
                order.Tif = ot.Tif;

            /*
             * Using FastMember for better performance than .Net reflection
             */
            // get prices from AFL scripts or OrderType directly

            // get MinTick for calculating prices comforming to OrderPlace
            decimal minTick = strategy != null ? strategy.Symbol.MinTick : -1;

            //get TypeAccessor
            TypeAccessor accessor = BaseOrderTypeAccessor.GetAccessor(orderType);

            MemberSet members = accessor.GetMembers();
            for (int i = 0; i < members.Count; i++)
            {
                Member member = members[i];
                string fieldName = "LmtPrice";
                if (member.Name == fieldName && orderType.Slippages.Count > 0)
                {
                    string str = (string)accessor[orderType, fieldName];
                    if (string.IsNullOrEmpty(str)) continue;
                    decimal val = 0;
                    if (double.TryParse(str, out double d))
                        val = (decimal)d;
                    else
                        val = strategy.CurrentPrices[str];

                    orgLmtPrice = val;
                    order.LimitPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                    orderType.RealPrices[fieldName] = val;
                    continue;
                }

                fieldName = "AuxPrice";
                if (member.Name == fieldName)
                {
                    string str = (string)accessor[orderType, fieldName];
                    if (string.IsNullOrEmpty(str)) continue;
                    decimal val = 0;
                    if (double.TryParse(str, out double d))
                        val = (decimal)d;
                    else
                        val = strategy.CurrentPrices[str];

                    order.AuxPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                    orderType.RealPrices[fieldName] = val;
                    continue;
                }

                fieldName = "TrailingPercent";
                if (member.Name == fieldName)
                {
                    string str = (string)accessor[orderType, fieldName];
                    if (string.IsNullOrEmpty(str)) continue;
                    decimal val = 0;
                    if (double.TryParse(str, out double d))
                        val = (decimal)d;
                    else
                        val = strategy.CurrentPrices[str];

                    order.TrailingPercent = (double)val;
                    orderType.RealPrices[fieldName] = val;
                    continue;
                }

                fieldName = "TrailStopPrice";
                if (member.Name == fieldName)
                {
                    string str = (string)accessor[orderType, fieldName];
                    if (string.IsNullOrEmpty(str)) continue;
                    decimal val = 0;
                    if (double.TryParse(str, out double d))
                        val = (decimal)d;
                    else
                        val = strategy.CurrentPrices[str];

                    order.TrailStopPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                    orderType.RealPrices[fieldName] = val;
                    continue;
                }
            }

            /*
            PropertyInfo[] pInfos = ot.GetType().GetProperties();
            PropertyInfo pi = pInfos.FirstOrDefault(x => x.Name == "LmtPrice");
            string aflVar = pi?.GetValue(ot)?.ToString();
            decimal minTick = strategy != null ? strategy.Symbol.MinTick : -1;
            if (pi != null)
            {
                if (aflVar != null)
                {
                    // order.LmtPrice = TruncatePrice(strategy.CurrentPrices[aflVar], minTick);
                    // keep value as to store original value as comparision                    
                    if (double.TryParse(aflVar, out double d))
                        order.LimitPrice = (decimal)d;
                    else
                        order.LimitPrice = (decimal)(strategy.CurrentPrices[aflVar]);
                }                    
                else
                    message += "Limit price is not available. ";
            }

            pi = pInfos.FirstOrDefault(x => x.Name == "AuxPrice");
            aflVar = pi?.GetValue(ot)?.ToString();
            if (pi != null)
            {
                if (aflVar != null)
                {
                    decimal val = 0;
                    if (!double.TryParse(aflVar, out double d))
                        val = (decimal)d;
                    else
                        val = strategy.CurrentPrices[aflVar];                    
                        
                    order.AuxPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                }
                else
                    message += "Auxarily price is not available. ";
            }

            pi = pInfos.FirstOrDefault(x => x.Name == "TrailingPercent");
            aflVar = pi?.GetValue(ot)?.ToString();
            if (pi != null)
            {
                if (aflVar != null)
                {
                    decimal val = 0;
                    if (!double.TryParse(aflVar, out double d))
                        val = (decimal)d;
                    else
                        val = strategy.CurrentPrices[aflVar];

                    order.TrailingPercent = (double)(minTick != -1 ? TruncatePrice(val, minTick) : val);
                }                    
                else
                    message += "Trailing precent is not available. ";
            }

            pi = pInfos.FirstOrDefault(x => x.Name == "TrailStopPrice");
            aflVar = pi?.GetValue(ot)?.ToString();
            if (pi != null)
            {
                if (aflVar != null)
                {
                    decimal val = 0;
                    if (!double.TryParse(aflVar, out double d))
                        val = (decimal)d;
                    else
                        val = strategy.CurrentPrices[aflVar];

                    order.TrailStopPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                }                
                else
                    message += "Trail stop price is not available.";
            }*/

            return order;
        }

        public bool ModifyOrder(AccountInfo accountInfo, Strategy strategy, OrderAction orderAction, BaseOrderType orderType)
        {
            try
            {
                string msg = string.Empty;
                bool result = false;

                string src = strategy.Symbol.Name + "." + strategy.Script.Name + "." + strategy.Name + "[ModifyOrder]";
                OrderInfo oi = strategy.AccountStat[accountInfo.Name].OrderInfos[orderAction].LastOrDefault();
                if (oi == null)
                {
                    MainVM.Log(new Log
                    {
                        Source = src,
                        Text = "Order info cannot be found in [" + accountInfo.Name + "]",
                        Time = DateTime.Now
                    });
                    return false;
                }

                if (oi.StopPriceRevisionDisallowed) return false;

                IEnumerable<OrderInfo> orderInfos = strategy.AccountStat[accountInfo.Name].OrderInfos[orderAction].Where(x => x.BatchNo == oi.BatchNo);

                // get all prices from new OrderType
                decimal lmtPrice = -1;
                decimal auxPrice = -1;
                decimal trailStopPrice = -1;
                double trailingPercent = -1;

                TypeAccessor accessor = BaseOrderTypeAccessor.GetAccessor(orderType);
                decimal minTick = strategy != null ? strategy.Symbol.MinTick : -1;
                decimal orgPrice = 0;

                MemberSet members = accessor.GetMembers();
                for (int i = 0; i < members.Count; i++)
                {
                    Member member = members[i];
                    string fieldName = "LmtPrice";
                    if (member.Name == fieldName && orderType.Slippages.Count > 0)
                    {
                        string str = (string)accessor[orderType, fieldName];
                        if (string.IsNullOrEmpty(str)) continue;
                        decimal val = 0;
                        if (double.TryParse(str, out double d))
                            val = (decimal)d;
                        else
                            val = strategy.CurrentPrices[str];

                        lmtPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                        orgPrice = lmtPrice;
                        orderType.RealPrices[fieldName] = val;
                        continue;
                    }

                    fieldName = "AuxPrice";
                    if (member.Name == fieldName)
                    {
                        string str = (string)accessor[orderType, fieldName];
                        if (string.IsNullOrEmpty(str)) continue;
                        decimal val = 0;
                        if (double.TryParse(str, out double d))
                            val = (decimal)d;
                        else
                            val = strategy.CurrentPrices[str];

                        auxPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                        orderType.RealPrices[fieldName] = val;
                        continue;
                    }

                    fieldName = "TrailingPercent";
                    if (member.Name == fieldName)
                    {
                        string str = (string)accessor[orderType, fieldName];
                        if (string.IsNullOrEmpty(str)) continue;
                        decimal val = 0;
                        if (double.TryParse(str, out double d))
                            val = (decimal)d;
                        else
                            val = strategy.CurrentPrices[str];

                        trailingPercent = (double)(minTick != -1 ? TruncatePrice(val, minTick) : val);
                        orderType.RealPrices[fieldName] = val;
                        continue;
                    }

                    fieldName = "TrailStopPrice";
                    if (member.Name == fieldName)
                    {
                        string str = (string)accessor[orderType, fieldName];
                        if (string.IsNullOrEmpty(str)) continue;
                        decimal val = 0;
                        if (double.TryParse(str, out double d))
                            val = (decimal)d;
                        else
                            val = strategy.CurrentPrices[str];

                        trailStopPrice = minTick != -1 ? TruncatePrice(val, minTick) : val;
                        orderType.RealPrices[fieldName] = val;
                        continue;
                    }
                }// END of update of order's prices

                // The size of slippages of original and new must be equal
                if (oi.OrderType.Slippages != null && orderType.Slippages != null
                    && oi.OrderType.Slippages.Count != orderType.Slippages.Count)
                {
                    MainVM.Log(new Log
                    {
                        Source = src,
                        Time = DateTime.Now,
                        Text = "ERROR: BatchNo[" + oi.BatchNo + "]: The sizes of Slipages are not equal",
                    });
                    return false;
                }

                List<int> slippages = new List<int>();
                List<int> posSizes = new List<int>();
                List<decimal> lmtPrices = new List<decimal>();
                foreach (var orderInfo in orderInfos.ToList())
                {
                    orderInfo.OrderLog.OrgPrice = lmtPrice;
                    Order order = orderInfo.Order;
                    decimal orgAuxPrice = order.AuxPrice;
                    double orgTrailingPercent = order.TrailingPercent;
                    decimal orgLmtPrice = order.LimitPrice;
                    decimal orgTrailStopPrice = order.TrailStopPrice;

                    posSizes.Add(order.TotalQuantity / orderInfo.Strategy.Symbol.RoundLotSize);
                    if (auxPrice > 0)
                        order.AuxPrice = auxPrice;
                    if (trailingPercent > 0)
                        order.TrailingPercent = trailingPercent;
                    if (trailStopPrice > 0)
                        order.TrailStopPrice = trailStopPrice;
                    if (lmtPrice > 0)
                    {
                        if (order.Action == ActionSide.Buy)
                            order.LimitPrice = lmtPrice + minTick * orderInfo.OrderLog.Slippage;
                        if (order.Action == ActionSide.Sell)
                            order.LimitPrice = lmtPrice - minTick * orderInfo.OrderLog.Slippage;
                        slippages.Add(orderInfo.OrderLog.Slippage);
                        lmtPrices.Add(order.LimitPrice);
                    }

                    // re-calc the quantity to prevent partially filled order being modified with original quantity
                    // order.TotalQuantity = (oi.OrderLog.PosSize - (int)oi.Filled) * oi.Strategy.Symbol.RoundLotSize;

                    Contract contract = orderInfo.Contract;
                    //(bool success, OrderLog orderLog) = PlaceOrder(orderInfo.RealOrderId, contract, order, true);
                    Client.PlaceOrder(orderInfo.RealOrderId, contract, order);
                    if (true)
                    {
                        orderInfo.OrderType = orderType;
                        orderInfo.PlacedTime = DateTime.Now;

                        DisplayedOrder displayedOrder = null;
                        lock (MainViewModel.ordersLock)
                        {
                            displayedOrder = MainVM.Orders.FirstOrDefault(x => x.OrderId == ConnParam.AccName + orderInfo.RealOrderId);
                        }

                        if (displayedOrder != null)
                        {
                            displayedOrder.StopPrice = order.AuxPrice;
                            displayedOrder.LmtPrice = order.LimitPrice;                            
                        }

                        msg = string.Format("Orders [{0}, {5}] was modified with stop price:{1}, lmt price:[{2}], slippages:[{3}], PosSizes:[{4}]",
                                string.Join(", ", orderInfos.Select(x => x.RealOrderId).ToList()),
                                auxPrice, string.Join(", ", lmtPrices), string.Join(", ", slippages), string.Join(", ", posSizes),
                                oi.OrderType.ToString());

                        result = true;
                    }
                    else
                    {
                        OrderLog orderLog = new OrderLog();
                        order.AuxPrice = orgAuxPrice;
                        order.LimitPrice = orgLmtPrice;
                        order.TrailingPercent = orgTrailingPercent;
                        order.TrailStopPrice = orgTrailStopPrice;
                        
                        msg = string.Format("Orders [{0}, {5}] modified (error: [{6}]) with stop price:{1}, lmt price:[{2}], slippages:[{3}], PosSizes:[{4}]",
                                string.Join(", ", orderInfos.Select(x => x.RealOrderId).ToList()),
                                auxPrice, string.Join(", ", lmtPrices), string.Join(", ", slippages), string.Join(", ", posSizes),
                                oi.OrderType.ToString(), orderLog.Error);
                        result = false;
                    }
                }

                /*
                if (quantity != orderInfo.OrderLog.PosSize * orderInfo.Strategy.Symbol.RoundLotSize)
                {
                    mainVM.Log(new Log
                    {
                        Source = "ModifyOrder",
                        Time = DateTime.Now,
                        Text = string.Format("Order Id [{0}] quantity modified with new pos:{1}, old pos:{2}",
                            orderInfo.RealOrderId, quantity / orderInfo.Strategy.Symbol.RoundLotSize,
                            orderInfo.OrderLog.PosSize)
                    });
                }*/

                MainVM.Log(new Log
                {
                    Source = src,
                    Time = DateTime.Now,
                    Text = msg
                });
                return result;
            }
            catch (Exception ex)
            {
                GlobalExceptionHandler.HandleException("IBController.ModifyOrder", ex);
                return false;
            }
        }

        public void SendCancledEvent(int orderId, int quantity)
        {
            OrderStatusEventArgs args = new OrderStatusEventArgs();
            args.Remaining = quantity;
            args.Filled = 0;
            args.Status = OrderStatus.ApiCancelled;
            args.OrderId = orderId;
            eh_OrderStatus(this, args);
        }

        public void Test(int orderId)
        {
            OrderInfo oi = MainVM.OrderInfoList[ConnParam.AccName + orderId];
            Order order = oi.Order.CloneObject();
            order.OrderType = OrderType.Market;
            Client.PlaceOrder(orderId, oi.Contract, order);
        }

        private void ModifyFailed()
        {
            int id = -1;
            string msg = string.Empty;

            foreach (var item in failedModification.ToArray())
            {
                lock (idLock)
                {
                    id = OrderIdCount++;

                    OrderInfo oi = item.Item1;
                    List<int> ids = item.Item2;
                    oi.OrderLog.OrderId = ConnParam.AccName + id;
                    oi.RealOrderId = id;

                    oi.RealOrderId = id;
                    oi.OrderId = ConnParam.AccName + id;

                    (bool success, OrderLog orderLog) = PlaceOrder(id, oi.Contract, oi.Order, true);
                    if (success)
                    {
                        OrderManager.AddBatchPosSize(oi.Account.Name + oi.BatchNo, id, oi.OrderLog.PosSize, 0, 0, 0, 0, false, ids);
                        oi.Strategy.AccountStat[oi.Account.Name].OrderInfos[oi.OrderAction].Add(oi);
                        MainVM.AddOrderInfo(oi.OrderId, oi);
                        msg = string.Format("Orders[{0}] have been modified as MarketOrder[{1}]", string.Join(",", ids), id);
                        failedModification.Remove(item);

                        MainVM.Log(new Log
                        {
                            Text = msg,
                            Source = "IBController.PatchForModificationFailures",
                            Time = DateTime.Now
                        });
                    }
                }
            }
        }

        // Modify incompleted order as Market Order
        private List<(OrderInfo, List<int>)> failedModification = new List<(OrderInfo, List<int>)>();
        private List<int> modifiedIds = new List<int>();
        public bool ModifyAsMarketOrder(IEnumerable<OrderInfo> orderInfos)
        {
            OrderInfo oi = null;
            List<int> ids = new List<int>();
            try
            {
                // combined all pending orders into one Market Order
                int quantity = 0;
                foreach (var orderInfo in orderInfos.ToList())
                {
                    if (modifiedIds.Contains(orderInfo.RealOrderId))
                    {
                        MainVM.MinorLog(new Log
                        {
                            Text = string.Format("OrderId [{0}] has been modified.", orderInfo.RealOrderId),
                            Time = DateTime.Now,
                            Source = "IBController.ModifiedOrder(Batch) - " + orderInfo.Strategy.Symbol.Name
                            + orderInfo.Strategy.Script.Name + orderInfo.Strategy.Name
                        });
                        continue;
                    }

                    if ((orderInfo.OrderStatus != null && orderInfo.OrderStatus.Remaining > 0) || orderInfo.OrderStatus == null)
                    {
                        quantity += orderInfo.OrderStatus != null ? 
                            (int)orderInfo.OrderStatus.Remaining * orderInfo.Strategy.Symbol.RoundLotSize :
                                orderInfo.OrderLog.PosSize * orderInfo.Strategy.Symbol.RoundLotSize;
                    }
                    oi = orderInfo;
                    ids.Add(orderInfo.RealOrderId);
                    modifiedIds.Add(orderInfo.RealOrderId);
                }

                if (quantity == 0) return true;

                string msg = string.Empty;
                bool result = false;

                Order order = oi.Order.CloneObject();
                order.TotalQuantity = quantity;
                order.OrderType = OrderType.Market;

                int id = 0;
                OrderLog olog = oi.OrderLog.CloneObject();
                lock (idLock)
                {
                    id = OrderIdCount++;

                    // update orderlog with new property values
                    olog.OrderId = ConnParam.AccName + id;
                    olog.RealOrderId = id;
                    olog.OrderSentTime = DateTime.Now;
                    olog.PosSize = quantity / oi.Strategy.Symbol.RoundLotSize;

                    OrderInfo info = new OrderInfo
                    {
                        RealOrderId = id,
                        OrderId = olog.OrderId,
                        BatchNo = oi.BatchNo, // must same as being canceled order
                        Strategy = oi.Strategy,
                        Account = oi.Account,
                        OrderAction = oi.OrderAction,
                        Contract = oi.Contract,
                        Order = order,
                        OrderType = new IBMarketOrder(),
                        OrderLog = olog,
                        IsAdjustedOrder = true
                    };

                    OrderManager.AddBatchPosSize(oi.Account.Name + info.BatchNo, id, quantity / oi.Strategy.Symbol.RoundLotSize, 0, 0, 0, 0, false, ids);
                    oi.Strategy.AccountStat[oi.Account.Name].OrderInfos[oi.OrderAction].Add(info);
                    MainVM.AddOrderInfo(olog.OrderId, info);

                    Client.PlaceOrder(id, oi.Contract, order);

                    msg = string.Format("Orders[{0}] have been modified as MarketOrder[{1}]", string.Join(",", ids), id);
                    result = true;

                    /*
                    (bool success, OrderLog orderLog) = PlaceOrder(id, oi.Contract, order, true);
                    if (success)
                    {
                        OrderManager.AddBatchPosSize(oi.Account.Name + info.BatchNo, id, quantity / oi.Strategy.Symbol.RoundLotSize, 0, 0, 0, 0, false, ids);
                        oi.Strategy.AccountStat[oi.Account.Name].OrderInfos[oi.OrderAction].Add(info);
                        MainVM.AddOrderInfo(olog.OrderId, info);
                        msg = string.Format("Orders[{0}] have been modified as MarketOrder[{1}]", string.Join(",", ids), id);
                        result = true;
                    }
                    else
                    {
                        failedModification.Add((info, ids));
                        msg = string.Format("Orders[{0}] modified as MarketOrder failed, error [{1}]", string.Join(",", ids), orderLog.Error);
                        result = false;
                    }*/
                }

                MainVM.Log(new Log
                {
                    Text = msg,
                    Source = "IBController.ModifyOrder(batch)",
                    Time = DateTime.Now
                });               

                return result;
            }
            catch (Exception ex)
            {
                GlobalExceptionHandler.HandleException("IBController.ModifyOrder(as Market)", ex);
                return false;
            }

        }

        public async Task<(int, OrderExecutionError)> PlaceOrderAsync(Contract contract, Order order)
        {
            try
            {
                int orderId = 0;
                OrderExecutionError error = OrderExecutionError.None;
                bool c = await IBTaskExt.FromEvent<EventArgs, bool>(
                handler =>
                {
                    Client.OrderStatus += new EventHandler<OrderStatusEventArgs>(handler);
                    Client.OpenOrder += new EventHandler<OpenOrderEventArgs>(handler);
                    Client.Error += new EventHandler<ErrorEventArgs>(handler);
                },
                (reqId) => { lock (idLock) {
                        orderId = OrderIdCount++;
                        order.OrderId = orderId;
                        /*
                        if (oi != null)
                        {
                            oi.OrderLog.RealOrderId = oi.RealOrderId = orderId;
                            oi.OrderLog.OrderId = oi.OrderId = ConnParam.AccName + orderId;                            
                            OrderManager.AddBatchPosSize(oi.Account.Name + oi.BatchNo, orderId, order.TotalQuantity / oi.Strategy.Symbol.RoundLotSize, 0, 0, 0, 0, false, ids);
                            oi.Strategy.AccountStat[oi.Account.Name].OrderInfos[oi.OrderAction].Add(oi);
                            MainVM.AddOrderInfo(oi.OrderLog.OrderId, oi);
                        }
                        */
                        OrderExecutionError err1 = OrderExecutionError.None;
                        if (ConnectionStatus == BrokerConnectionStatus.Disconnected)
                            err1 = OrderExecutionError.NotConnected;
                        if (ConnectionStatus == BrokerConnectionStatus.Error)
                            err1 = OrderExecutionError.ConnectionError;

                        if (err1  == OrderExecutionError.None)
                            Client.PlaceOrder(orderId, contract, order);
                        return (orderId, OrderStatus.None, err1);
                    }
                },
                handler =>
                {
                    Client.OpenOrder -= new EventHandler<OpenOrderEventArgs>(handler);
                    Client.OrderStatus -= new EventHandler<OrderStatusEventArgs>(handler);
                    Client.Error -= new EventHandler<ErrorEventArgs>(handler);
                },
                CancellationToken.None);
                if (c)
                    return (orderId, OrderExecutionError.None);
                else
                    return (-1, error);
            }
            catch (Exception ex)
            {
                //throw ex;
                GlobalExceptionHandler.HandleException("IBControler.CancelOrderAsync", ex);
                return (-1, OrderExecutionError.Exception);
            }
        }
        // modify the price to conform to minTick requirement
        private decimal TruncatePrice(decimal price, decimal minTick, PriceAlignDirection priceAlign = PriceAlignDirection.Floor)
        {
            decimal redundant = price % minTick;
            decimal modifiedPrice = priceAlign == PriceAlignDirection.Ceiling ? price - redundant + minTick : price - redundant;
            return modifiedPrice;
        }

        private (bool, OrderLog) PlaceOrder(int orderId, Contract contract, Order order, bool isModifying = false)
        {
            OrderLog orderLog = new OrderLog();
            if (ConnectionStatus != BrokerConnectionStatus.Connected)
            {
                if (!isModifying)
                {
                    // Issue a faked 
                    OpenOrderEventArgs openOrderEventArgs = new OpenOrderEventArgs();
                    OrderState orderState = new OrderState();
                    order.OrderId = orderId;
                    orderState.Status = OrderStatus.Error;
                    openOrderEventArgs.OrderState = orderState;
                    openOrderEventArgs.Contract = contract;
                    openOrderEventArgs.Order = order;
                    openOrderEventArgs.OrderId = orderId;
                    eh_OpenOrder(this, openOrderEventArgs);
                    // Issue a faked Canceled event
                    OrderStatusEventArgs args = new OrderStatusEventArgs();
                    args.Filled = 0;
                    args.Remaining = order.TotalQuantity;
                    args.Status = OrderStatus.ApiCancelled;
                    args.OrderId = orderId;
                    args.WhyHeld = OrderExecutionError.ConnectionError.ToString() + " - " + ConnectionStatus.ToString();
                    eh_OrderStatus(this, args);
                }

                orderLog.Error = string.Format("Connection error: {0}", ConnectionStatus.ToString());
                orderLog.OrderId = "-1";
                return (false, orderLog);
            }
            else
            {
                Client.PlaceOrder(orderId, contract, order);
                return (true, null);
            }
        }

        /// <summary>
        /// Place order, return OrderId if successful, otherwise, return -1
        /// </summary>
        /// <param name="accountInfo"></param>
        /// <param name="symbol"></param>
        /// <returns>-1 means failure</returns>
        /// TODO:
        /// 
        public async Task<List<OrderLog>> PlaceOrder(AccountInfo accountInfo, Strategy strategy, BaseOrderType orderType,
            OrderAction orderAction, int batchNo, double? posSize = null, Contract security = null,
            bool errorSuppressed = false, bool addToInfoList = true)
        {
            try
            {
                /*
                // When a long position opened, 
                // If there is a future short order, position size must be added for reverse exit
                // oi saves the short order info
                if (oi0 != null)
                {
                    int oid = 0;
                    lock (idLock)
                    {
                        oid = OrderIdCount++;
                        Client.PlaceOrder(oid, oi0.Contract, oi0.Order);
                    }
                    OrderLog olog = new OrderLog();
                    olog.RealOrderId = oid;
                    olog.OrderId = ConnParam.AccName + oid;
                    olog.OrderSentTime = DateTime.Now;

                    // add into list immediately to prevent from adding later than error hanlding,
                    // which leads to order status cannot be reverted as expected
                    if (strategy != null && addToInfoList)
                    {
                        OrderInfo oi1 = oi0.CloneObject();
                        MainViewModel.Instance.OrderInfoList.Add(olog.OrderId, oi1);
                        oi0.Strategy.AccountStat[oi0.Account.Name].OrderInfos[oi0.OrderAction].Add(oi1);
                    }

                    List<OrderLog> logs = new List<OrderLog>();
                    logs.Add(olog);
                    return logs;
                }*/
                //if (MainVM.OrderInfoList[])
                OrderLog orderLog = new OrderLog();

                string message = string.Empty;
                Order order = new Order();
                // original limit price
                decimal orgLmtPrice = -1; //used for displaying info in Log for double-checking

                if (posSize == null)
                {
                    // prevent caculating TotalQuantity
                    if (orderType.Slippages != null && orderType.Slippages.Count > 0)
                        posSize = -1;
                }

                // save order for another IBController use
                if (Orders.ContainsKey(batchNo))
                    order = Orders[batchNo];
                else
                {
                    order = TransformIBOrder(accountInfo, strategy, orderType, orderAction, out message, out orgLmtPrice, posSize);
                    order.OutsideRth = strategy == null ? true : strategy.OutsideRTH;
                    Orders.Add(batchNo, order);
                }
                
                orderLog.Error = message;
                if (message != string.Empty && !errorSuppressed)
                {
                    orderLog.OrderId = "-1";
                    return new List<OrderLog> { orderLog };
                }

                Contract contract = new Contract();
                if (security == null)
                {
                    string symbolName = strategy.Symbol.SymbolDefinition.FirstOrDefault(x => x.Controller.Vendor == Vendor)?.ContractId;
                    string[] parts = symbolName.Split(new char[] { '-' });
                    switch (parts.Length)
                    {
                        case 1:
                            contract.LocalSymbol = parts[0];
                            contract.Exchange = "SMART";
                            contract.SecurityType = SecurityType.Stock;
                            break;
                        case 2:
                            contract.LocalSymbol = parts[0];
                            contract.Exchange = parts[1];
                            contract.SecurityType = SecurityType.Stock;
                            break;
                        case 3:
                            contract.LocalSymbol = parts[0];
                            contract.Exchange = parts[1];
                            contract.SecurityType = IBContract.SecTypeConverter(parts[2]);
                            break;
                        case 4:
                            contract.LocalSymbol = parts[0];
                            contract.Exchange = parts[1];
                            contract.SecurityType = IBContract.SecTypeConverter(parts[2]);
                            contract.Currency = parts[3];
                            break;
                    }
                    IBContract iBContract = await ((IBController)accountInfo.Controller).reqContractDetailsAsync(contract);
                    if (iBContract == null)
                    {
                        MainVM.Log(new Log
                        {
                            Text = "No security definition found for " + contract.LocalSymbol,
                            Source = "IBController.PlaceOrder",
                            Time = DateTime.Now
                        });
                        orderLog.OrderId = "-1";
                        orderLog.Success = false;
                        orderLog.Error = "No security definition found for " + contract.LocalSymbol;
                        return new List<OrderLog> { orderLog };
                    }
                    contract = iBContract.Contract;
                }
                else
                {
                    contract = security;
                    if (contract.Exchange == null)
                        contract.Exchange = contract.PrimaryExchange;

                }

                if (contract != null)
                {
                    List<OrderLog> orderLogs = new List<OrderLog>();

                    // get position size from either AFL or default value
                    float _ps = 0;
                    if (strategy != null)
                    {
                        if (!string.IsNullOrEmpty(orderType.PositionSize))
                            _ps = (float)(strategy.CurrentPosSize[orderType.PositionSize]);
                        else
                            _ps = strategy.DefaultPositionSize;
                    }

                    // calculate total position size
                    int ttlPosSize = 0;
                    if (strategy != null)
                    {
                        if (orderAction == OrderAction.Buy || orderAction == OrderAction.Short)
                            ttlPosSize = (int)_ps;
                        else if (orderAction == OrderAction.Cover || orderAction == OrderAction.APSShort || orderAction == OrderAction.StoplossShort
                                || orderAction == OrderAction.FinalForceExitShort || orderAction == OrderAction.PreForceExitShort)
                            ttlPosSize = Math.Min((int)strategy.AccountStat[accountInfo.Name].ShortPosition, (int)_ps);
                        else if (orderAction == OrderAction.Sell || orderAction == OrderAction.APSLong || orderAction == OrderAction.StoplossLong
                                || orderAction == OrderAction.FinalForceExitLong || orderAction == OrderAction.PreForceExitLong)
                            ttlPosSize = Math.Min((int)strategy.AccountStat[accountInfo.Name].LongPosition, (int)_ps);
                    }

                    // in case of slippage table defined
                    if (orderType.Slippages != null && orderType.Slippages.Count > 0)
                    {
                        // assign position size according to the pre-defined slippage table
                        int round = 0;
                        int accuPosSize = 0;    // accumulated position size
                        List<CSlippage> slippages = new List<CSlippage>();
                        List<int> posSizes = new List<int>();
                        for (int i = 0; i < orderType.Slippages.Count; i++)
                        {
                            int ps = 0;
                            int ps_pre = orderType.Slippages[i].PosSize;
                            if (accuPosSize + ps_pre <= ttlPosSize)
                                ps = ps_pre;
                            else
                                ps = ttlPosSize - accuPosSize;

                            if (round == 0)
                            {
                                slippages.Add(orderType.Slippages[i]);
                                posSizes.Add(ps);
                            }
                            else
                                posSizes[i] += ps;

                            accuPosSize += ps;
                            if (accuPosSize == ttlPosSize) break;
                            if (accuPosSize > ttlPosSize) throw new Exception("Error in calculating Position Size (IBController.PlaceOrder)");

                            if (accuPosSize < ttlPosSize && i == orderType.Slippages.Count - 1)
                            {
                                i = -1;
                                round++;
                            }
                        }
                        // END of re-arrangement of position size

                        decimal _orgLmtP = order.LimitPrice;
                        for (int i = 0; i < slippages.Count; i++)
                        {
                            int ps = posSizes[i];
                            CSlippage slippage = orderType.Slippages[i];
                            OrderLog olog = new OrderLog();
                            olog.Error = message;

                            // assign actual position size
                            order.TotalQuantity = (int)(ps * strategy.Symbol.RoundLotSize);

                            if (order.Action == ActionSide.Buy)
                            {
                                order.LimitPrice = _orgLmtP + slippage.Slippage * strategy.Symbol.MinTick;
                            }
                            else if (order.Action == ActionSide.Sell)
                            {
                                order.LimitPrice = _orgLmtP - slippage.Slippage * strategy.Symbol.MinTick;
                            }

                            // update OrderLog
                            olog.OrgPrice = orgLmtPrice;
                            olog.LmtPrice = order.LimitPrice;
                            olog.AuxPrice = order.AuxPrice;
                            olog.TrailingPercent = order.TrailingPercent;
                            olog.TrailStopPrice = order.TrailStopPrice;
                            olog.OrderSentTime = DateTime.Now;
                            olog.PosSize = ps;
                            olog.Slippage = slippage.Slippage;
                            // add to orderLogs as return value
                            //orderLogs.Add(olog);

                            // sent order
                            int orderId = 0;
                            if (!ConnParam.IsMulti)
                            {
                                //using (await m_lock.LockAsync())
                                lock (idLock)
                                {
                                    orderId = OrderIdCount++;

                                    olog.RealOrderId = orderId;
                                    olog.OrderId = ConnParam.AccName + orderId;

                                    // add into list immediately to prevent from adding later than error hanlding,
                                    // which leads to order status cannot be reverted as expected
                                    if (strategy != null && addToInfoList)
                                    {
                                        OrderInfo oi = new OrderInfo
                                        {
                                            RealOrderId = olog.RealOrderId,
                                            OrderId = olog.OrderId,
                                            BatchNo = batchNo,
                                            Strategy = strategy,
                                            Account = accountInfo,
                                            OrderAction = orderAction,
                                            //TotalPosSize = ttlPosSize,
                                            Contract = contract,
                                            Order = order.CloneObject(),
                                            OrderType = orderType,
                                            OrderLog = olog
                                        };
                                        OrderManager.AddBatchPosSize(accountInfo.Name + oi.BatchNo, olog.RealOrderId, olog.PosSize);
                                        MainViewModel.Instance.AddOrderInfo(olog.OrderId, oi);
                                    }

                                    // place order
                                    (bool success, OrderLog log) = PlaceOrder(orderId, contract, order);
                                    if (!success)
                                        orderLogs.Add(log);
                                    else
                                        orderLogs.Add(olog);
                                }
                            }
                            else
                            {
                                // if multi instances are running, valid order id must be obtained from IB server
                                // this will degrade the performance seriously
                                orderId = await reqIdsAsync();

                                olog.RealOrderId = orderId;
                                olog.OrderId = ConnParam.AccName + orderId;

                                // add into list immediately to prevent from adding later than error hanlding,
                                // which leads to order status cannot be reverted as expected
                                if (strategy != null && addToInfoList)
                                {
                                    OrderInfo oi = new OrderInfo
                                    {
                                        RealOrderId = olog.RealOrderId,
                                        OrderId = olog.OrderId,
                                        BatchNo = batchNo,
                                        Strategy = strategy,
                                        Account = accountInfo,
                                        OrderAction = orderAction,
                                        //TotalPosSize = ttlPosSize,
                                        Contract = contract,
                                        Order = order.CloneObject(),
                                        OrderType = orderType,
                                        OrderLog = olog
                                    };
                                    OrderManager.AddBatchPosSize(accountInfo.Name + oi.BatchNo, olog.RealOrderId, olog.PosSize);
                                    MainViewModel.Instance.AddOrderInfo(olog.OrderId, oi);
                                }

                                (bool success, OrderLog log) = PlaceOrder(orderId, contract, order);
                                if (!success)
                                    orderLogs.Add(log);
                                else
                                    orderLogs.Add(olog);
                            }
                        }
                    }
                    else
                    // no slippage defined for e.g. MarketOrder or fixed LmtPrice
                    {
                        int orderId = 0;
                        if (strategy != null)
                            order.TotalQuantity = (int)(ttlPosSize * strategy.Symbol.RoundLotSize);
                        else
                            order.TotalQuantity = (int)orderType.TotalQuantity;

                        // update OrderLog
                        orderLog.OrgPrice = orgLmtPrice;
                        orderLog.OrderSentTime = DateTime.Now;
                        // strategy.PosSize cannot be used here because PosSize may not equal to that number.
                        orderLog.PosSize = strategy != null ? (int)(order.TotalQuantity / strategy.Symbol.RoundLotSize) : order.TotalQuantity;
                        //orderLogs.Add(orderLog);

                        if (!ConnParam.IsMulti)
                        {
                            //using (await m_lock.LockAsync())
                            lock (idLock)
                            {
                                orderId = OrderIdCount++;

                                orderLog.RealOrderId = orderId;
                                orderLog.OrderId = ConnParam.AccName + orderId;

                                // add into list immediately to prevent from adding later than error hanlding,
                                // which leads to order status cannot be reverted as expected
                                if (strategy != null && addToInfoList)
                                {
                                    OrderInfo oi = new OrderInfo
                                    {
                                        RealOrderId = orderLog.RealOrderId,
                                        OrderId = orderLog.OrderId,
                                        BatchNo = batchNo,
                                        Strategy = strategy,
                                        Account = accountInfo,
                                        OrderAction = orderAction,
                                        Contract = contract,
                                        Order = order.CloneObject(),
                                        OrderType = orderType,
                                        OrderLog = orderLog
                                    };
                                    OrderManager.AddBatchPosSize(accountInfo.Name + oi.BatchNo, orderLog.RealOrderId, orderLog.PosSize);
                                    MainViewModel.Instance.AddOrderInfo(orderLog.OrderId, oi);
                                }

                                (bool success, OrderLog log) = PlaceOrder(orderId, contract, order);
                                if (!success)
                                    orderLogs.Add(log);
                                else
                                    orderLogs.Add(orderLog);
                            }
                        }
                        else
                        {
                            // if multi instances are running, valid order id must be obtained from IB server
                            // this will degrade the performance seriously
                            orderId = await reqIdsAsync();

                            orderLog.RealOrderId = orderId;
                            orderLog.OrderId = ConnParam.AccName + orderId;

                            // add into list immediately to prevent from adding later than error hanlding,
                            // which leads to order status cannot be reverted as expected
                            if (strategy != null && addToInfoList)
                            {
                                OrderInfo oi = new OrderInfo
                                {
                                    RealOrderId = orderLog.RealOrderId,
                                    OrderId = orderLog.OrderId,
                                    BatchNo = batchNo,
                                    Strategy = strategy,
                                    Account = accountInfo,
                                    OrderAction = orderAction,
                                    Contract = contract,
                                    Order = order.CloneObject(),
                                    OrderType = orderType,
                                    OrderLog = orderLog
                                };
                                OrderManager.AddBatchPosSize(accountInfo.Name + oi.BatchNo, orderLog.RealOrderId, orderLog.PosSize);
                                MainViewModel.Instance.AddOrderInfo(orderLog.OrderId, oi);
                            }

                            (bool success, OrderLog log) = PlaceOrder(orderId, contract, order);
                            if (!success)
                                orderLogs.Add(log);
                            else
                                orderLogs.Add(orderLog);
                        }


                    }

                    return orderLogs;
                }
                else
                {
                    orderLog.OrderId = "-1";
                    orderLog.Error = "Contract cannot be found.";
                    return new List<OrderLog> { orderLog };
                }
            }
            catch (Exception ex)
            {
                GlobalExceptionHandler.HandleException("IBController.PlaceOrder", ex);
                OrderLog orderLog = new OrderLog();
                orderLog.OrderId = "-1";
                orderLog.Error = ex.Message;
                return new List<OrderLog> { orderLog };
            }
        }

        private static void AddCancelingOrders(int orderId)
        {
            if (CancelingOrders.ContainsKey(orderId))
            {
                CancelingOrders[orderId].LastTime = DateTime.Now;
                CancelingOrders[orderId].Times++;
            }
            else
                CancelingOrders.Add(orderId, new CancelingOrder());
        }

        public void CancelOrder(int orderId)
        {
            lock (idLock)
            {
                AddCancelingOrders(orderId);
                Client.CancelOrder(orderId);
            }
        }
        public async Task<(bool, OrderExecutionError)> CancelOrderAsync(int orderId)
        {
            try
            {
                (bool c, OrderExecutionError error) = await IBTaskExt.FromEvent<EventArgs, (bool, OrderExecutionError)>(
                handler =>
                {
                    Client.OrderStatus += new EventHandler<OrderStatusEventArgs>(handler);
                    Client.Error += new EventHandler<ErrorEventArgs>(handler);
                },
                (reqId) => { lock (idLock) {
                        OrderInfo oi = null;
                        if (MainVM.OrderInfoList.ContainsKey(ConnParam.AccName + orderId))
                            oi = MainVM.OrderInfoList[ConnParam.AccName + orderId];

                        OrderExecutionError err1 = OrderExecutionError.None;
                        if (ConnectionStatus == BrokerConnectionStatus.Disconnected)
                            err1 = OrderExecutionError.NotConnected;
                        if (ConnectionStatus == BrokerConnectionStatus.Error)
                            err1 = OrderExecutionError.ConnectionError;

                        if (err1 == OrderExecutionError.None)
                        {
                            AddCancelingOrders(orderId);
                            Client.CancelOrder(orderId);
                        }
                            
                        return (-1, oi != null && oi.OrderStatus != null ? oi.OrderStatus.Status : OrderStatus.None, err1);
                    }
                },
                handler =>
                {
                    Client.OrderStatus -= new EventHandler<OrderStatusEventArgs>(handler);
                    Client.Error -= new EventHandler<ErrorEventArgs>(handler);
                },
                CancellationToken.None, orderId);
                return (c, error);
            }
            catch (Exception ex)
            {
                //throw ex;
                GlobalExceptionHandler.HandleException("IBControler.CancelOrderAsync", ex);
                return (false, OrderExecutionError.Exception);
            }
        }

        //
        // cancel orders with same batch no
        //
        public async Task<bool> CancelOrdersAsync(OrderInfo oi)
        {
            try
            {
                IEnumerable<OrderInfo> orderInfos = oi.Strategy.AccountStat[oi.Account.Name].OrderInfos[oi.OrderAction]
                    .Where(x => x.BatchNo == oi.BatchNo);
                foreach (var orderInfo in orderInfos.ToList())
                {
                    if ((orderInfo.OrderStatus != null && orderInfo.OrderStatus.Remaining > 0) || orderInfo.OrderStatus == null)
                    {
                        await CancelOrderAsync(orderInfo.RealOrderId);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                //throw ex;
                GlobalExceptionHandler.HandleException("IBControler.CancelOrderAsync", ex);
                return false;
            }
        }

        //
        // cancel orders with same batch no
        //
        public bool CancelOrders(OrderInfo oi)
        {
            try
            {
                if (oi == null)
                {
                    MainVM.Log(new Log
                    {
                        Text = "Cancellation failed due to OrderInfo is null.",
                        Source = "Controller.CancelOrders" + oi.Strategy.Symbol.Name + "." +
                        oi.Strategy.Script.Name + "." + oi.Strategy.Name,
                        Time = DateTime.Now
                    });
                    return false;
                }
                IEnumerable<OrderInfo> orderInfos = oi.Strategy.AccountStat[oi.Account.Name].OrderInfos[oi.OrderAction]
                    .Where(x => x.BatchNo == oi.BatchNo);
                foreach (var orderInfo in orderInfos.ToList())
                {
                    if ((orderInfo.OrderStatus != null && orderInfo.OrderStatus.Remaining > 0) || orderInfo.OrderStatus == null)
                    {
                        AddCancelingOrders(orderInfo.RealOrderId);
                        Client.CancelOrder(orderInfo.RealOrderId);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                //throw ex;
                GlobalExceptionHandler.HandleException("IBControler.CancelOrderAsync", ex);
                return false;
            }
        }

        // auto reconnect
        private System.Timers.Timer timer = new System.Timers.Timer();
        private void AutoReconnect()
        {
            // isconnected must be put in the first place
            if (IsConnected || disconnectByManual || timer.Enabled) return;
            int interval = MainVM.UserPreference.ConnectAttempInterval;
            if (interval > 0 && MainVM.UserPreference.ReconnectEnabled)
            {
                timer.Interval = interval * 1000;
                timer.Elapsed += Timer_Elapsed;
                timer.Start();
            }
        }
        private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!MainVM.UserPreference.ReconnectEnabled)
            {
                timer.Stop();
                return;
            }
            if (IsConnected) return;
            MainVM.AddMessage(new Message { Time = DateTime.Now, Text = "Trying to reconnect..." });
            if (!IsConnected)
                Connect();

            timer.Stop();
        }

        public void Disconnect()
        {
            Client.Disconnect();
            disconnectByManual = true;
            if (!Client.Connected)
                ConnectionStatus = BrokerConnectionStatus.Disconnected;
        }

        public void Connect()
        {
            try
            {
                disconnectByManual = false;
                Client.Connect(ConnParam.Host, ConnParam.Port, ConnParam.ClientId);
                if (Client.Connected)
                {
                    Request();
                }
            }
            catch (Exception ex)
            {
                MainVM.AddMessage(new Message
                {
                    Time = DateTime.Now,
                    Text = "Exception: " + ex.Message
                });
                AutoReconnect();
            }
        }
        private void eh_ManagedAccounts(object sender, ManagedAccountsEventArgs e)
        {
            List<string> accounts = e.AccountsList.Split(new char[] { ',' }).OfType<string>().ToList();
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                foreach (var acc in accounts)
                {
                    if (!string.IsNullOrEmpty(acc))
                    {
                        AccountInfo acc1 = Accounts.FirstOrDefault<AccountInfo>(x => x.Name == acc);
                        if (acc1 == null)
                            Accounts.Add(new AccountInfo(acc, this));
                    }
                    // make the first one as selected account
                    if (SelectedAccount == null && Accounts.Count > 0)
                        SelectedAccount = Accounts[0];
                }
                // remove out-of-date accounts
                foreach (var acc in Accounts.ToList())
                {
                    string accName = accounts.FirstOrDefault(x => x == acc.Name);
                    if (string.IsNullOrEmpty(accName))
                        Accounts.Remove(Accounts.FirstOrDefault(x => x.Name == acc.Name));
                }
            });
            // reqAccountUpdate to get portfolio for each account
            for (int i = 0; i < accounts.Count(); i++)
            {
                Client.RequestAccountUpdates(true, accounts[i]);
                Thread.Sleep(1000);
            }
            //Client.RequestAccountUpdates(true, accounts[0]);
            last_req_account = accounts[0];
        }
        private void eh_OpenOrder(object sender, OpenOrderEventArgs e)
        {
            MainVM.MinorLog(new Log()
            {
                Text = string.Format("Open order status - Id: {0}, status:{1}",
                                                e.OrderId, e.OrderState.Status),
                Source = "IB API(order status event)",
                Time = DateTime.Now
            });

            OrderInfo oi = null;
            if (MainVM.OrderInfoList.ContainsKey(ConnParam.AccName + e.OrderId))
            {
                oi = MainVM.OrderInfoList[ConnParam.AccName + e.OrderId];
            }
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                DisplayedOrder dOrder = null;
                lock (MainViewModel.ordersLock)
                {
                    dOrder = MainVM.Orders.FirstOrDefault<DisplayedOrder>(x => x.OrderId == ConnParam.AccName + e.OrderId);
                }

                if (dOrder == null)
                {
                    dOrder = new DisplayedOrder()
                    {
                        RealOrderId = e.Order.OrderId,
                        OrderId = ConnParam.AccName + e.Order.OrderId,
                        Action = e.Order.Action.ToString(),
                        Type = e.Order.OrderType.ToString(),
                        Symbol = e.Contract.Symbol,
                        Currency = e.Contract.Currency,
                        Status = e.OrderState.Status,
                        Account = e.Order.Account,
                        Tif = e.Order.Tif.ToString(),
                        GTD = e.Order.GoodTillDate,
                        GAT = e.Order.GoodAfterTime,
                        StopPrice = e.Order.AuxPrice,
                        LmtPrice = e.Order.OrderType.ToString().ToLower().Contains("limit") ? e.Order.LimitPrice : 0,
                        Quantity = e.Order.TotalQuantity,
                        Remaining = e.Order.TotalQuantity / (oi != null ? oi.Strategy.Symbol.RoundLotSize : 1),
                        Exchange = e.Contract.Exchange,
                        ParentId = e.Order.ParentId,
                        OcaGroup = e.Order.OcaGroup,
                        OcaType = e.Order.OcaType.ToString(),
                        Source = DisplayName,
                        Time = DateTime.Now,
                        Contract = e.Contract,
                        Vendor = Vendor
                    };
                    if (oi != null)
                    {
                        dOrder.PlacedTime = oi.OrderLog.OrderSentTime;
                        Strategy strategy = oi.Strategy;
                        if (strategy != null)
                            dOrder.Strategy = strategy.Symbol.Name + "." + strategy.Script.Name + "." + strategy.Name
                                            + "." + oi.OrderLog.Slippage;
                    }                    
                    MainVM.InsertOrder(dOrder);
                }
            });

        }

        private void eh_ExecDetails(object sender, ExecDetailsEventArgs e)
        {
            OrderInfo oi = null;
            int filled = 0;
            int remaining = 0;
            if (MainVM.OrderInfoList.ContainsKey(ConnParam.AccName + e.OrderId))
            {
                oi = MainVM.OrderInfoList[ConnParam.AccName + e.OrderId];
                filled = e.Execution.Shares / oi.Strategy.Symbol.RoundLotSize;
                remaining = oi.OrderLog.PosSize - filled;
            }
            else
                return;

            OrderStatusEventArgs args = new OrderStatusEventArgs();
            args.OrderId = e.OrderId;
            args.AverageFillPrice = e.Execution.AvgPrice;
            args.ClientId = e.Execution.ClientId;
            args.Status = oi.OrderLog.PosSize == filled ? OrderStatus.Filled : OrderStatus.Submitted;
            args.PermId = e.Execution.PermId;
            args.Filled = e.Execution.Shares;
            args.Remaining = remaining * oi.Strategy.Symbol.RoundLotSize;
            args.WhyHeld = "Send by ExecDetails";
            eh_OrderStatus(this, args);
        }
        private void eh_OrderStatus(object sender, OrderStatusEventArgs e)
        {
            // current filled/remaining
            int filled = 0;
            int remaining = 0;
            OrderInfo oi = null;
            if (MainVM.OrderInfoList.ContainsKey(ConnParam.AccName + e.OrderId))
            {
                oi = MainVM.OrderInfoList[ConnParam.AccName + e.OrderId];
                filled = e.Filled / oi.Strategy.Symbol.RoundLotSize;
                remaining = e.Remaining / oi.Strategy.Symbol.RoundLotSize;
            }
            else
            {
                filled = e.Filled;
                remaining = e.Remaining;
            }


            MainVM.MinorLog(new Log()
            {
                Text = string.Format("Coming order status - Id: {0}, filled: {1}, remaining: {2}, status:{3}, Source:{4}, WhyHeld:{5}",
                                                e.OrderId, filled, remaining, e.Status, sender, e.WhyHeld),
                Source = "IB API(order status event)",
                Time = DateTime.Now
            });

            if (oi != null && oi.OrderStatus != null && (oi.OrderStatus.Status == OrderStatus.Canceled || oi.OrderStatus.Status == OrderStatus.ApiCancelled) && e.Status == OrderStatus.Filled)
            {
                string errLog = string.Format("Error: a Filled event was raised on a canceled order, previous status[{0}]",
                    oi.OrderStatus.Status);
                MainVM.Log(new Log
                {
                    Text = errLog,
                    Time = DateTime.Now,
                    Source = string.Format("On OrderStatus Error (OrderId[{0}], [{0}])", oi.RealOrderId, oi.Strategy.Symbol.Name + oi.Strategy.Name)
                });
                return;
            }

            DisplayedOrder dOrder = null;
            lock (MainViewModel.ordersLock)
            {
                dOrder = MainVM.Orders.LastOrDefault<DisplayedOrder>(x => x.OrderId == ConnParam.AccName + e.OrderId);
            }
            if (dOrder == null)
            {
                MainVM.Log(new Log() { Text = string.Format("Order Id: {0:0} cannot be found", e.OrderId), Time = DateTime.Now });
            }
            else
            {
                // make a copy if every step needs keeping
                if (MainVM.UserPreference.KeepTradeSteps)
                {
                    DisplayedOrder tmp = null;
                    if (MainVM.UserPreference.IgnoreDuplicatedRecord)
                    {
                        lock (MainViewModel.ordersLock)
                        {
                            tmp = MainVM.Orders.LastOrDefault(x => x.OrderId == ConnParam.AccName + e.OrderId &&
                                                                x.Remaining == remaining && x.Status == e.Status);
                        }
                        if (tmp != null)
                            dOrder = tmp;
                    }
                    if (tmp == null)
                    {
                        dOrder = dOrder.ShallowCopy();
                        dOrder.Time = DateTime.Now;                        
                        //if (oi != null)
                        {
                            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                            {
                                dOrder.Status = e.Status;
                                dOrder.Filled = filled;
                                dOrder.Remaining = remaining;
                                dOrder.AvgPrice = e.AverageFillPrice;
                                dOrder.Time = DateTime.Now;
                                MainVM.InsertOrder(dOrder);
                            });
                        }
                    }
                }
                Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                {
                    dOrder.Status = e.Status;
                    dOrder.Filled = filled;
                    dOrder.Remaining = remaining;
                    dOrder.AvgPrice = e.AverageFillPrice;
                    dOrder.Time = DateTime.Now;
                    if (oi != null)
                        dOrder.PlacedTime = oi.OrderLog.OrderSentTime;
                });
            }

            if (oi != null)
            {
                if (dOrder != null)
                {
                    // prevent duplicated order status
                    DisplayedOrder displayedOrder = null;
                    lock (MainViewModel.updatedOrdersLock)
                    {
                        displayedOrder = MainVM.UpdatedOrders.LastOrDefault(x => x.Filled == filled &&
                                                          x.OrderId == ConnParam.AccName + e.OrderId &&
                                                          x.Remaining == remaining);
                    }

                    // in case partially filled order canceled, (PendingCancel or Submitted) and Canceled will be issued with same filled and remaining
                    bool duplicatedFound = false;

                    if (displayedOrder != null)
                    {
                        /*
                        if (displayedOrder.Status == OrderStatus.PreSubmitted && e.Status != OrderStatus.Canceled)
                        {
                            Order o = mainVM.OrderInfoList[ConnParam.AccName + e.OrderId].Order;
                            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                            {
                                dOrder.StopPrice = o.AuxPrice;
                                dOrder.LmtPrice = o.LimitPrice;
                            });
                        }*/
                        // previous status: partially filled
                        // current status: canceled
                        // will result in same filled and canceled amount
                        // need flagging here

                        if (displayedOrder.Status != e.Status && (e.Status == OrderStatus.ApiCancelled || e.Status == OrderStatus.Canceled))
                        {
                            duplicatedFound = true;
                            // update status
                            oi.OrderStatus = dOrder;
                            MainVM.InsertUpdatedOrder(dOrder);
                        }
                        else
                        {
                            MainVM.MinorLog(new Log()
                            {
                                Text = string.Format("Duplicated order status found for order Id: {0}, filled: {1}, remaining: {2}, current status:{3}, previous status:{4}",
                                                e.OrderId, filled, remaining, e.Status, displayedOrder.Status),
                                Source = "IB API(order status event)",
                                Time = DateTime.Now
                            });

                            if (displayedOrder.Status != e.Status && displayedOrder.Status != OrderStatus.Filled &&
                                displayedOrder.Status != OrderStatus.Canceled && displayedOrder.Status != OrderStatus.ApiCancelled)
                            {
                                // update status
                                oi.OrderStatus = dOrder;
                                MainVM.InsertUpdatedOrder(dOrder);
                            }
                            return;
                        }
                    }
                    else
                    {
                        MainVM.InsertUpdatedOrder(dOrder);
                    }

                    oi.OrderStatus = dOrder;
                    BaseStat strategyStat = oi.Strategy.AccountStat[oi.Account.Name];
                    Script script = oi.Strategy.Script;
                    Strategy strategy = oi.Strategy;
                    AdaptiveProfitStop apsLong = oi.Strategy.AdaptiveProfitStopforLong;
                    AdaptiveProfitStop apsShort = oi.Strategy.AdaptiveProfitStopforShort;
                    BaseStat scriptStat = script.AccountStat[oi.Account.Name];
                    dOrder.PlacedTime = oi.OrderLog.OrderSentTime;

                    switch (e.Status)
                    {
                        case OrderStatus.Inactive:
                        case OrderStatus.Error:
                            break;
                        case OrderStatus.PreSubmitted:
                        case OrderStatus.PendingSubmit:
                        case OrderStatus.Submitted:
                        case OrderStatus.PendingCancel:
                        case OrderStatus.ApiCancelled:
                        case OrderStatus.Canceled:
                        case OrderStatus.Filled:
                        case OrderStatus.PartiallyFilled:

                            oi.Filled = filled;

                            // get previous stored position
                            int prev_filled = 0;
                            int prev_remaining = 0;
                            prev_filled = OrderManager.BatchPosSize[oi.Account.Name + oi.BatchNo].Filled;
                            prev_remaining = OrderManager.BatchPosSize[oi.Account.Name + oi.BatchNo].Remaining;

                            // sending alter message if filled or canceled
                            if (e.Status == OrderStatus.Filled && MainVM.UserPreference.AltersHandling.OrderFilled)
                            {
                                string alterStr = string.Format("Symbol[{0}] OrderId[{4}] has been filled on account[{1}], Position:{2}, AvgPrice:{3}",
                                    oi.Strategy.Symbol.Name, oi.Account.DisplayName, filled, e.AverageFillPrice, e.OrderId);
                                AlterHandler.SendMessage(alterStr);
                            }
                            if ((e.Status == OrderStatus.Canceled || e.Status == OrderStatus.ApiCancelled) && MainVM.UserPreference.AltersHandling.OrderCancel)
                            {
                                string alterStr = string.Format("Symbol[{0}] OrderId[{3}] has been canceled on account[{1}], Position:{2}",
                                    oi.Strategy.Symbol.Name, oi.Account.DisplayName, remaining, e.OrderId);
                                AlterHandler.SendMessage(alterStr);
                            }


                            // update batch PosSize
                            if (e.Status == OrderStatus.ApiCancelled || e.Status == OrderStatus.Canceled)
                            {
                                // if duplicated found, only remaining will be passed as canceled amount
                                // since filled has been passed when PartiallyFilled issued
                                //if (duplicatedFound)
                                // only canceled should be calculated
                                // OrderManager.AddBatchPosSize(oi.Account.Name + oi.BatchNo, oi.RealOrderId, 0, 0, 0, remaining);
                                //else
                                OrderManager.AddBatchPosSize(oi.Account.Name + oi.BatchNo, oi.RealOrderId, 0, filled, 0, remaining, e.AverageFillPrice, oi.ModifiedAsMarketOrder);
                            }
                            else
                                OrderManager.AddBatchPosSize(oi.Account.Name + oi.BatchNo, oi.RealOrderId, 0, filled, remaining, 0, e.AverageFillPrice);


                            int cur_filled = OrderManager.BatchPosSize[oi.Account.Name + oi.BatchNo].Filled;
                            int cur_remaining = OrderManager.BatchPosSize[oi.Account.Name + oi.BatchNo].Remaining;
                            decimal avgPrice = OrderManager.BatchPosSize[oi.Account.Name + oi.BatchNo].AvgPrice;

                            double prev_long_pos = strategyStat.LongPosition;
                            double prev_short_pos = strategyStat.ShortPosition;
                            MainVM.MinorLog(new Log
                            {
                                Text = string.Format("Staus:{8}, OrderAction:{7}, Status:{6}, prev_filled:{0}, prev_re:{1}, " +
                                "current_filled:{2}, current_re:{3}, long position:{10}, short position:{11}, filled:{4}, remaining:{5}, duplicated:{9}",
                                prev_filled, prev_remaining, cur_filled, cur_remaining, filled, remaining, dOrder.Status.ToString(),
                                oi.OrderAction.ToString(), strategyStat.Status.ToString(), duplicatedFound.ToString(),
                                strategyStat.LongPosition, strategyStat.ShortPosition),
                                Source = string.Format("BatchNo:{0}, OrderId:{1}", oi.BatchNo, oi.RealOrderId.ToString()),
                                Time = DateTime.Now,
                            });

                            if (true)
                            {
                                if (oi.OrderAction == OrderAction.Buy)
                                {
                                    strategyStat.LongPosition = cur_filled;
                                    scriptStat.LongPosition += cur_filled - prev_filled;

                                    strategyStat.LongAvgPrice = (double)avgPrice;
                                    apsLong.EntryPrice = (float)avgPrice; // (float)e.AverageFillPrice;
                                    oi.Strategy.ForceExitOrderForLong.EntryPrice = (float)avgPrice;
                                    Entry entry = new Entry(oi.OrderId, oi.BatchNo);
                                    strategyStat.LongEntry.Add(entry);
                                    scriptStat.LongEntry.Add(entry);
                                }
                                else if (oi.OrderAction == OrderAction.Short)
                                {
                                    strategyStat.ShortPosition = cur_filled;
                                    scriptStat.ShortPosition += cur_filled - prev_filled;

                                    strategyStat.ShortAvgPrice = (double)avgPrice;
                                    apsShort.EntryPrice = (float)avgPrice; // (float)e.AverageFillPrice;
                                    oi.Strategy.ForceExitOrderForShort.EntryPrice = (float)avgPrice;
                                    // adding entry
                                    Entry entry = new Entry(oi.OrderId, oi.BatchNo);
                                    strategyStat.ShortEntry.Add(entry);
                                    scriptStat.ShortEntry.Add(entry);
                                }

                                else if (oi.OrderAction == OrderAction.Sell || oi.OrderAction == OrderAction.APSLong
                                    || oi.OrderAction == OrderAction.StoplossLong || oi.OrderAction == OrderAction.PreForceExitLong
                                    || oi.OrderAction == OrderAction.FinalForceExitLong)
                                {
                                    strategyStat.LongPosition -= cur_filled - prev_filled;
                                    scriptStat.LongPosition -= cur_filled - prev_filled;
                                }

                                else if (oi.OrderAction == OrderAction.Cover || oi.OrderAction == OrderAction.APSShort
                                    || oi.OrderAction == OrderAction.StoplossShort || oi.OrderAction == OrderAction.PreForceExitShort
                                    || oi.OrderAction == OrderAction.FinalForceExitShort)
                                {
                                    strategyStat.ShortPosition -= cur_filled - prev_filled;
                                    scriptStat.ShortPosition -= cur_filled - prev_filled;
                                }
                            }

                            // update account status if canceled
                            if ((e.Status == OrderStatus.ApiCancelled || e.Status == OrderStatus.Canceled) && filled == 0)
                                AccountStatusOp.RevertActionStatus(strategyStat, scriptStat, strategy, oi.OrderAction, oi.BatchNo, true);
                            // Update Account Status if filled or partially filled
                            else if (filled > 0)
                                AccountStatusOp.SetPositionStatus(strategyStat, scriptStat, strategy, oi.OrderAction, oi.BatchNo, oi);

                            MainVM.MinorLog(new Log
                            {
                                Text = string.Format("After status updated: {0}, long position:{1}, short position:{2}, IsCompleted:{3}",
                                strategyStat.Status.ToString(), strategyStat.LongPosition, strategyStat.ShortPosition,
                                OrderManager.BatchPosSize[oi.Account.Name + oi.BatchNo].IsCompleted),
                                Source = string.Format("BatchNo:{0}, OrderId:{1}", oi.BatchNo, oi.RealOrderId.ToString()),
                                Time = DateTime.Now,
                            });
                            break;

                    }
                }
            }
            else
            {
                //throw new Exception("OrderId:" + e.OrderId + " not found");
                //Exception ex = new Exception("OrderId:" + e.OrderId + " not found");
                //GlobalExceptionHandler.HandleException("IBController.eh_OrderStatus", ex);
                MainVM.Log(new Log() { Text = string.Format("Order Id: {0:0} cannot be found, this order may be issued mannually", e.OrderId), Time = DateTime.Now });
            }
        }
        private void eh_UpdatePortfolio(object sender, UpdatePortfolioEventArgs e)
        {
            bool isInPortfolio = true;
            string symbol_name = e.Contract.Symbol;
            SymbolInMkt symbol = MainVM.Portfolio.FirstOrDefault<SymbolInMkt>(x => x.Symbol == symbol_name &&
                                                                                x.Account == e.AccountName);
            if (symbol == null)
            {
                symbol = new SymbolInMkt();
                isInPortfolio = false;
            }
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                if (isInPortfolio)
                {
                    symbol.MktValue = e.MarketValue;
                    symbol.AvgCost = e.AverageCost;
                    symbol.MktPrice = e.MarketPrice;
                    symbol.Position = e.Position;
                    symbol.RealizedPNL = e.RealizedPnl;
                    symbol.UnrealizedPNL = e.UnrealizedPnl;

                }
                else
                {
                    symbol.Symbol = e.Contract.Symbol;
                    symbol.Currency = e.Contract.Currency;
                    symbol.Account = e.AccountName;
                    symbol.AvgCost = e.AverageCost;
                    symbol.MktPrice = e.MarketPrice;
                    symbol.MktValue = e.MarketValue;
                    symbol.Position = e.Position;
                    symbol.RealizedPNL = e.RealizedPnl;
                    symbol.UnrealizedPNL = e.UnrealizedPnl;
                    symbol.Source = DisplayName;
                    symbol.Vendor = Vendor;
                    symbol.Contract = e.Contract;
                    MainVM.Portfolio.Insert(0, symbol);
                }
            });

            double long_pos = 0;
            double short_pos = 0;
            double pos = 0;
            foreach (var item in MainVM.SymbolInActions)
            {                
                SymbolDefinition sd = item.SymbolDefinition.FirstOrDefault(x => x.Controller.Vendor == Vendor);
                if (sd != null && sd.Contract != null && sd.Contract.Symbol == e.Contract.Symbol
                    && sd.Contract.Currency == e.Contract.Currency && sd.Contract.Expiry == sd.Contract.Expiry)
                {
                    pos = e.Position / item.RoundLotSize;
                    foreach (var script in item.Scripts)
                    {
                        foreach (var acc in Accounts)
                        {
                            if (script.AccountStat.ContainsKey(acc.Name))
                            {
                                long_pos += script.AccountStat[acc.Name].LongPosition;
                                short_pos += script.AccountStat[acc.Name].ShortPosition;
                            }
                        }
                    }
                }
            }

            string msg = string.Empty;
            if ((pos > 0 && long_pos != pos) ||
                (pos < 0 && short_pos != -pos) ||
                (pos == 0 && (long_pos != 0 || short_pos != 0)))
            {
                MainVM.Log(new Log
                {
                    Text = string.Format("[{5}][{3}] Symbol[{4}] positions are not equal - Portfolio[{0}], Long Position[{1}], Short Position[{2}]",
                    pos, long_pos, short_pos, e.AccountName, e.Contract.Symbol, DisplayName),
                    Source = "UpdatePortfolio",
                    Time = DateTime.Now
                });
            }
        }
        private decimal GetAVgFilledPrice(OrderInfo oi)
        {
            decimal avgPrice = 0;
            SymbolDefinition sd = oi.Strategy.Symbol.SymbolDefinition.FirstOrDefault(x => x.Controller.Vendor == Vendor);
            foreach (var item in MainVM.Portfolio)
            {                
                if (sd != null && sd.Contract != null && sd.Contract.Symbol == item.Symbol
                    && sd.Contract.Currency == item.Currency)
                {
                    avgPrice = item.MktPrice;
                    break;
                }
            }
            return avgPrice;
        }
        private void eh_Error(object sender, ErrorEventArgs e)
        {
            string msg = e.ErrorMsg;
            string log = string.Empty;
            OrderInfo oi = null;
            lock (MainViewModel.orderInfoListLock)
            {
                oi = MainVM.OrderInfoList.FirstOrDefault(x => x.Value.OrderId == ConnParam.AccName + e.TickerId).Value;
            }
            if (oi != null)
            {
                oi.Error = e.ErrorMsg;
                if (oi.Strategy != null && oi.Strategy.AccountStat.ContainsKey(oi.Account.Name))
                {
                    OrderStatusEventArgs args = new OrderStatusEventArgs();
                    Strategy strategy = oi.Strategy;
                    BaseStat strategyStat = oi.Strategy.AccountStat[oi.Account.Name];
                    BaseStat scriptStat = oi.Strategy.Script.AccountStat[oi.Account.Name];
                    string prevStatus = string.Join(",", Helper.TranslateAccountStatus(strategyStat.AccountStatus));

                    bool canceledByError = false;
                    if (!string.IsNullOrEmpty(e.ErrorMsg))
                    {
                        OrderExecutionError error = IBErrorHandling.ErrorHandling(e.ErrorMsg);
                        // ignore future placed order error message
                        if (e.ErrorMsg.ToLower().Contains("exception") || e.ErrorMsg.ToLower().Contains("notconnected"))
                            AccountStatusOp.RevertActionStatus(strategyStat, scriptStat, strategy, oi.OrderAction, oi.BatchNo);

                        if (error == OrderExecutionError.StopPriceRevisionDisallowed)
                            oi.StopPriceRevisionDisallowed = true;
                        
                        if (error == OrderExecutionError.IdNotFoundOnCancel && CancelingOrders.ContainsKey(e.TickerId)
                            && oi.OrderStatus == null)
                        {
                            args.Filled = 0;
                            args.Remaining = oi.OrderLog.PosSize * strategy.Symbol.RoundLotSize;
                            args.Status = OrderStatus.ApiCancelled;
                            args.OrderId = e.TickerId;
                            args.WhyHeld = error.ToString();
                            eh_OrderStatus(this, args);
                            canceledByError = true;
                            log = "A faked ApiCancelled event has been sent\n" + e.ErrorMsg;
                        }

                        if (error == OrderExecutionError.CannotCancelFilledOrder || error == OrderExecutionError.CannotModifyFilledOrder
                            || error == OrderExecutionError.IdNotFoundOnCancel || error == OrderExecutionError.AlreadyFilled
                            || (error == OrderExecutionError.DuplicateOrderId && oi.OrderStatus != null))
                        {
                            if ((!CancelingOrders.ContainsKey(e.TickerId) || 
                                (CancelingOrders.ContainsKey(e.TickerId) && (DateTime.Now - CancelingOrders[e.TickerId].LastTime).TotalSeconds > 3))
                                && oi.OrderStatus != null && oi.OrderStatus.Status != OrderStatus.Canceled && oi.OrderStatus.Status != OrderStatus.ApiCancelled)
                            {
                                args.Filled = oi.OrderLog.PosSize * strategy.Symbol.RoundLotSize;
                                args.Remaining = 0;
                                args.AverageFillPrice = GetAVgFilledPrice(oi);
                                args.Status = OrderStatus.Filled;
                                args.OrderId = e.TickerId;
                                args.WhyHeld = error.ToString();
                                eh_OrderStatus(this, args);
                                log = string.Format("OrderId[{0}] - A faked Filled event has been sent - AvgPrice[{2}].\n{1}",
                                    e.TickerId, e.ErrorMsg, args.AverageFillPrice);
                            }
                            else
                            {
                                string txt = string.Empty;
                                if (!CancelingOrders.ContainsKey(e.TickerId) ||
                                (CancelingOrders.ContainsKey(e.TickerId) && (DateTime.Now - CancelingOrders[e.TickerId].LastTime).TotalSeconds > 3))
                                    txt = string.Format("Error: an incorrent event was raised on a cancelling order, current error[{0}] [{1}]", error.ToString(), e.ErrorMsg);
                                else if (oi.OrderStatus != null)
                                    txt = string.Format("Error: an incorrent event was raised on a {2} order, current error[{0}] [{1}]", error.ToString(), 
                                        e.ErrorMsg, oi.OrderStatus.Status.ToString());

                                MainVM.Log(new Log
                                {
                                    Text = txt,
                                    Time = DateTime.Now,
                                    Source = string.Format("On Error [{0}] IB API Event", ConnParam.AccName),
                                });
                            }
                        }

                        // deal with Duplicated Order Id
                        // deal with disappeared PreSubmitted orders
                        if ((error == OrderExecutionError.DuplicateOrderId && oi.OrderStatus == null) ||
                            error == OrderExecutionError.AlreadyCanceled ||
                            error == OrderExecutionError.PriceNotConformed ||
                            error == OrderExecutionError.InsufficientEquity ||
                            error == OrderExecutionError.ExchangeClosed ||
                            error == OrderExecutionError.OrderSizeError ||

                            (error == OrderExecutionError.IdNotFoundOnCancel && CancelingOrders.ContainsKey(e.TickerId)
                            && oi.OrderStatus == null))
                        {
                            // insert a makeup DisplayOrder into mainVM.Orders if not found
                            DisplayedOrder dOrder = null;
                            lock (MainViewModel.ordersLock)
                            {
                                dOrder = MainVM.Orders.FirstOrDefault<DisplayedOrder>(x => x.OrderId == ConnParam.AccName + e.TickerId);
                            }
                            if (dOrder == null)
                            {
                                dOrder = MakeDisplayedOrder(e.TickerId);
                                MainVM.InsertOrder(dOrder);
                            }

                            // issue an ApiCanceled event
                            if (oi.OrderStatus == null || oi.OrderStatus.Status == OrderStatus.Inactive ||
                                oi.OrderStatus.Status == OrderStatus.PreSubmitted)
                            {
                                args.Filled = oi.OrderStatus == null ? 0 : (int)oi.OrderStatus.Filled;
                                args.Remaining = oi.OrderStatus == null ? oi.OrderLog.PosSize * strategy.Symbol.RoundLotSize :
                                    (int)oi.OrderStatus.Remaining * strategy.Symbol.RoundLotSize;
                                args.Status = OrderStatus.ApiCancelled;
                                args.OrderId = e.TickerId;
                                args.WhyHeld = error.ToString();
                                eh_OrderStatus(this, args);
                                canceledByError = true;
                                log = string.Format("OrderId[{0} - A faked ApiCancelled event has been sent.\n{1}",
                                    e.TickerId ,e.ErrorMsg);
                            }
                        }
                    }

                    Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                    {
                        MainVM.Log(new Log()
                        {
                            Time = DateTime.Now,
                            Text = e.ErrorCode + "-" + e.ErrorMsg + "(OrderId:" + oi.OrderId + ", previous status:[" + prevStatus
                            + "], current status:[" + string.Join(",", Helper.TranslateAccountStatus(strategyStat.AccountStatus)) + "])"
                            + (canceledByError ? "\n" : ""),
                            Source = "OnError (" + oi.Strategy.Script.Symbol.Name + "." + oi.Strategy.Name + "." + oi.OrderLog.Slippage +")"
                        });
                    });
                }
                else
                {
                    Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                    {
                        MainVM.Log(new Log()
                        {
                            Time = DateTime.Now,
                            Text = e.ErrorMsg + "(OrderId:" + oi.OrderId + ", error: account status not found)",
                            Source = "OnError " + oi.Strategy != null ? " (" + oi.Strategy.Script.Symbol.Name + "." + oi.Strategy.Name + "." + oi.OrderLog.Slippage + ")"
                                    : "Source not found, may be issued mannually."
                        });
                    });
                }

                if (!string.IsNullOrEmpty(log))
                {
                    Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                    {
                        MainVM.Log(new Log()
                        {
                            Time = DateTime.Now,
                            Text = log,
                            Source = "OnError (" + oi.Strategy.Script.Symbol.Name + "." + oi.Strategy.Name + "." + oi.OrderLog.Slippage + ")"
                        });
                    });
                }
            }
            else if (e.TickerId > 0)
            {
                int i = 0;
                Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                {
                    MainVM.Log(new Log()
                    {
                        Time = DateTime.Now,
                        Text = "(orderId not found)" + e.ErrorMsg,
                        Source = "OnError (TickerId:" + e.TickerId.ToString() + ")"
                    });
                });
            }
            if (e.ErrorMsg != null && (e.ErrorMsg.Contains("Connectivity between IB and Trader Workstation has been lost")
                || e.ErrorMsg.Contains("Connectivity between Trader Workstation and server is broken")
                || (e.ErrorMsg.Contains("Connectivity between") && e.ErrorMsg.Contains("has been lost"))))
            {
                ConnectionStatus = BrokerConnectionStatus.Error;
                if (MainVM.UserPreference.AltersHandling.ConnectionStatus)
                    AlterHandler.SendMessage(e.ErrorMsg);
            }
            if (e.ErrorMsg != null && (e.ErrorMsg.Contains("Connectivity between IB and Trader Workstation has been restored")
                || (e.ErrorMsg.ToLower().Contains("connectivity") && e.ErrorMsg.ToLower().Contains("restore"))))
            {
                ConnectionStatus = BrokerConnectionStatus.Connected;
                if (MainVM.UserPreference.AltersHandling.ConnectionStatus)
                    AlterHandler.SendMessage(e.ErrorMsg);
            }
            if (e.ErrorMsg != null && e.ErrorMsg.ToLower().Contains("data farm connection is ok"))
            {
                ConnectionStatus = BrokerConnectionStatus.Connected;
            }
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                MainVM.AddMessage(new Message()
                {
                    Time = DateTime.Now,
                    //Code = e.ErrorCode.,
                    Text = e.TickerId != -1 ? "Request ID:" + e.TickerId + ", msg:" + msg : msg,
                    Source = DisplayName
                });
            });
            if ((int)e.ErrorCode == 326)
            {
                ConnParam.ClientId++;
                //Connect();
            }
        }
        private void eh_ConnectionClosed(object sender, ConnectionClosedEventArgs e)
        {
            ConnectionStatus = BrokerConnectionStatus.Disconnected;
            if (!disconnectByManual)
            {
                if (!SystemHelper.IsTWSOpen())
                {
                    MainVM.AddMessage(new Message
                    {
                        Time = DateTime.Now,
                        Text = "Connection closed due to TWS shutdown."
                    });
                }
                else
                {
                    MainVM.AddMessage(new Message
                    {
                        Time = DateTime.Now,
                        Text = "Connection closed due to unknown reason - sender:" + sender.ToString()
                    });
                }
                AutoReconnect();
            }
        }
        // refresh portfolio in a fixed period
        private void RefreshPortfolio()
        {
            if (Accounts.Count <= 1) return;

            foreach (var item in MainVM.Portfolio.ToList())
            {
                if (item.Position > 0 && Accounts.Any(x => x.Name == item.Account))
                {
                    Client.RequestAccountUpdates(true, item.Account);
                    Thread.Sleep(1000);
                    last_req_account = item.Account;
                    MainVM.MinorLog(new Log
                    {
                        Text = string.Format("Account [{0}] has been refreshed", item.Account),
                        Source = "RefreshPortfolio",
                        Time = DateTime.Now
                    });
                }
            }
        }
        // make up a virtual DisplayedOrder
        private DisplayedOrder MakeDisplayedOrder(int orderId)
        {
            OrderInfo oi = null;
            lock (MainViewModel.orderInfoListLock)
            {
                oi = MainVM.OrderInfoList.FirstOrDefault(x => x.Value.OrderId == ConnParam.AccName + orderId).Value;
            }
            DisplayedOrder dOrder = null;
            if (oi != null)
            {
                dOrder = new DisplayedOrder()
                {
                    RealOrderId = orderId,
                    OrderId = ConnParam.AccName + orderId,
                    Action = oi.Order.Action.ToString(),
                    Type = oi.Order.OrderType.ToString(),
                    Symbol = oi.Contract.Symbol,
                    Currency = oi.Contract.Currency,
                    Status = OrderStatus.Error,
                    Account = oi.Account.Name,
                    StopPrice = oi.Order.AuxPrice,
                    LmtPrice = oi.Order.LimitPrice,
                    Quantity = oi.Order.TotalQuantity,
                    Remaining = oi.Order.TotalQuantity,
                    Exchange = oi.Contract.Exchange,
                    ParentId = oi.Order.ParentId,
                    OcaGroup = oi.Order.OcaGroup,
                    OcaType = oi.Order.OcaType.ToString(),
                    Source = DisplayName,
                    Time = DateTime.Now,
                    Contract = oi.Contract,
                    Vendor = Vendor,
                    Strategy = oi.Strategy.Symbol.Name + "." + oi.Strategy.Script.Name + "." + oi.Strategy.Name
                                            + "." + oi.OrderLog.Slippage
                };
            }
            else
                throw new Exception("No order info found for OrderId:" + orderId);

            return dOrder;
        }

        private bool init = false;
        private void Init()
        {
            if (init) return;
            init = true;
            // place an order impossible traded for JITed performance
            Log log = new Log { Time = DateTime.Now, Text = "Faked order generated." };
            MainVM.Log(log);
            SymbolInAction symbol = new SymbolInAction("QQQ", 60);
            symbol.AppliedControllers.Add(this);
            symbol.MinTick = 0.01M;
            symbol.RoundLotSize = 10;
            Script script = new Script("script1", symbol);
            symbol.Scripts.Add(script);
            Strategy strategy = new Strategy("strategy1", script);
            script.Strategies.Add(strategy);
            strategy.DefaultPositionSize = 3;
            IBLimitOrder orderType = new IBLimitOrder();
            //orderType.Slippage = 0;
            strategy.BuyOrderTypes.Add(orderType);
            int bn = OrderManager.BatchNo;

            PlaceOrder(Accounts[0], strategy, orderType, OrderAction.Buy, bn, null, null, true, false);
            Log log1 = new Log { Time = DateTime.Now, Text = "Faked order1 placed." };
            MainVM.Log(log1);
            //Client.CancelOrder(ol1.OrderId);
            /*
            orderType.Slippages.Add(new CSlippage { PosSize = 1, Slippage = 1 });
            orderType.Slippages.Add(new CSlippage { PosSize = 1, Slippage = 2 });
            List<OrderLog> logs = await PlaceOrder(Accounts[0], strategy, orderType, OrderAction.Buy, bn, null, null, null, true, false);
            foreach (OrderLog olog in logs)
            {
                mainVM.Log(new Log { Time = olog.OrderSentTime, Text = "Faked order2 placed." + olog.Slippage });
            }
            

            orderType.Slippages.Add(new CSlippage { PosSize = 1, Slippage = 3 });
            orderType.Slippages.Add(new CSlippage { PosSize = 1, Slippage = 4 });
            logs = await PlaceOrder(Accounts[0], strategy, orderType, OrderAction.Buy, bn, null, null, null, true, false);
            foreach (OrderLog olog in logs)
            {
                mainVM.Log(new Log { Time = olog.OrderSentTime, Text = "Faked order3 placed." + olog.Slippage });
            }*/
            //ClientSocket.cancelOrder(ol1.OrderId);
            //ClientSocket.cancelOrder(ol2.OrderId);
            //ClientSocket.cancelOrder(ol3.OrderId);
        }
        private async void AfterConnected(int id = -1)
        {
            _hub.Publish<IController>(this);
            ConnectionStatus = BrokerConnectionStatus.Connected;
            if (id == -1)
            {
                id = await reqIdsAsync();
            }
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                MainVM.AddMessage(new Message()
                {
                    Source = DisplayName,
                    Time = DateTime.Now,
                    Text = "Connected -- NextValidOrderID: " + id
                });
                if (OrderIdCount < id)
                    OrderIdCount = id;
            });
            Init();
        }
        private void eh_AccountValue(object sender, UpdateAccountValueEventArgs e)
        {
            AccountInfo acc = Accounts.FirstOrDefault<AccountInfo>(x => x.Name == e.AccountName);
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                if (acc == null)
                {
                    acc = new AccountInfo(e.AccountName, this);
                    Accounts.Add(acc);
                    MainVM.Log(new Log()
                    {
                        Source = DisplayName,
                        Time = DateTime.Now,
                        Text = string.Format("A new account [{0}] has been added", e.AccountName)
                    });
                }
                AccountTag tag = acc.Properties.FirstOrDefault<AccountTag>(x => x.Tag == e.Key);
                if (tag == null)
                    acc.Properties.Add(new AccountTag() { Tag = e.Key, Currency = e.Currency, Value = e.Value });
                else
                    tag.Value = e.Value;
            });
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public static class IBErrorHandling
    {
        public static OrderExecutionError ErrorHandling(string msg, int code = -1)
        {
            OrderExecutionError error = OrderExecutionError.None;
            if (string.IsNullOrEmpty(msg)) return OrderExecutionError.EmptyErrorMsg;
            // deal with insufficent buying power or the exchange is closed (limit order -> market order occurs during exchange closed)                        
            
            if (msg.ToLower().Contains("equity") && 
                (msg.ToLower().Contains("insufficient") || msg.ToLower().Contains("must exceed")))
                error = OrderExecutionError.InsufficientEquity;
            if (msg.ToLower().Contains("the exchange is closed"))
                error = OrderExecutionError.ExchangeClosed;

            if (msg.ToLower().Contains("can't modify a filled order") || msg.ToLower().Contains("cannot modify the filled order"))
                error = OrderExecutionError.CannotModifyFilledOrder;
            if (msg.ToLower().Contains("can't cancel a filled order"))
                error = OrderExecutionError.CannotCancelFilledOrder;
            if (msg.ToLower().Contains("duplicate order id") || code == (int)OrderExecutionError.DuplicateOrderId)
                error = OrderExecutionError.DuplicateOrderId;

            if (msg.ToLower().Contains("stop price revision is disallowed"))
                error = OrderExecutionError.StopPriceRevisionDisallowed;

            if (msg.ToLower().Contains("needs to be cancelled is not found"))
                error = OrderExecutionError.IdNotFoundOnCancel;
            if (msg.ToLower().Contains("state: cancelled"))
                error = OrderExecutionError.AlreadyCanceled;
            if (msg.ToLower().Contains("state: pendingcancel"))
                error = OrderExecutionError.PendingCancel;
            if (msg.ToLower().Contains("state: filled"))
                error = OrderExecutionError.AlreadyFilled;

            if (msg.ToLower().Contains("order size cannot be zero"))
                error = OrderExecutionError.OrderSizeError;

            if (msg.ToLower().Contains("order canceled"))
                error = OrderExecutionError.Canceled;

            if (msg.ToLower().Contains("the price does not conform to the minimum price variation"))
                error = OrderExecutionError.PriceNotConformed;

            if (error == OrderExecutionError.None)
                error = OrderExecutionError.Unknown;

            return error;
        }
    }

    public static class IBTaskExt
    {
        private static int reqIdCount = -10000;

        public static async Task<T> FromEvent<TEventArgs, T>(
            Action<EventHandler<TEventArgs>> registerEvent,
            Func<int, (int, OrderStatus, OrderExecutionError)> func,
            Action<EventHandler<TEventArgs>> unregisterEvent,
            CancellationToken token,
            object parameter = null,
            int? timeout = null)
        {
            int orderId = -1;
            OrderStatus prevOrderStatus = OrderStatus.None;
            OrderExecutionError funcError = OrderExecutionError.None;   // Execution error returned by function, ie. ConnectionError
            string accName = string.Empty;

            int reqId = reqIdCount--;
            if (reqIdCount <= int.MinValue)
                reqIdCount = -10000;

            var tcs = new TaskCompletionSource<T>();
            if (timeout != null)
            {
                var ct = new CancellationTokenSource((int)timeout);
                ct.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
            }

            Contract contract = new Contract();
            EventHandler<TEventArgs> handler = (sender, args) =>
            {
                if (args.GetType() == typeof(ErrorEventArgs))
                {
                    ErrorEventArgs arg = args as ErrorEventArgs;
                    if (arg.TickerId == reqId)
                    {
                        Exception ex = new Exception(arg.ErrorMsg);
                        ex.Data.Add("RequestId", arg.TickerId);
                        ex.Data.Add("ErrorCode", arg.ErrorCode);
                        ex.Data.Add("Message", arg.ErrorMsg);
                        ex.Source = "IBTaskExt.FromEvent";
                        tcs.TrySetException(ex);
                    }
                    // for order cancellation
                    if (parameter != null && parameter.GetType() == typeof(int) && (int)parameter == arg.TickerId)
                    {
                        bool? result = null;
                        OrderExecutionError error = IBErrorHandling.ErrorHandling(arg.ErrorMsg);

                        if (error == OrderExecutionError.AlreadyCanceled ||
                        error == OrderExecutionError.CannotCancelFilledOrder ||
                        error == OrderExecutionError.ConnectionError ||
                        error == OrderExecutionError.DuplicateOrderId ||
                        error == OrderExecutionError.NotConnected ||
                        error == OrderExecutionError.IdNotFoundOnCancel ||
                        error == OrderExecutionError.AlreadyFilled)
                            result = false;

                        // in case of order in the middle of cancellation
                        else if (error == OrderExecutionError.PendingCancel && prevOrderStatus == OrderStatus.PendingCancel)
                            result = false;
                        else if (error == OrderExecutionError.Canceled || error == OrderExecutionError.CannotModifyFilledOrder)
                            result = true;                                                    
                        // wait if cancellation in progress
                        else
                            result = null;
                        
                        if (result != null && !(bool)result)
                            MainViewModel.Instance.Log(new Log
                            {
                                Text = string.Format("Order[{0}] canceled failed due to [{2}] {1}", arg.TickerId, arg.ErrorMsg, error. ToString()),
                                Source = "CancelOrderAsync",
                                Time = DateTime.Now
                            });

                        if (result != null)
                            tcs.TrySetResult((T)(object)((bool)result, error));
                        else
                            return;
                    }
                    // for order place
                    if (arg.TickerId == orderId)
                    {
                        bool result = false;
                        OrderExecutionError error = IBErrorHandling.ErrorHandling(arg.ErrorMsg);

                        if (error == OrderExecutionError.Unknown)
                            result = true;

                        if (!result)
                            MainViewModel.Instance.Log(new Log
                            {
                                Text = string.Format("Order[{0}] placement failed due to [{2}] {1}", arg.TickerId, arg.ErrorMsg, error.ToString()),
                                Source = "PlaceOrderAsync",
                                Time = DateTime.Now
                            });

                        tcs.TrySetResult((T)(object)(result, error));
                    }
                }
                else if (args.GetType() == typeof(ContractDetailsEventArgs))
                {
                    ContractDetailsEventArgs arg = args as ContractDetailsEventArgs;
                    if (arg.RequestId == reqId)
                    {
                        contract.ContractId = arg.ContractDetails.Summary.ContractId;
                        //contract.LastTradeDateOrContractMonth = arg.ContractDetails.Summary.LastTradeDateOrContractMonth;
                        contract.LocalSymbol = arg.ContractDetails.Summary.LocalSymbol;
                        contract.SecurityType = arg.ContractDetails.Summary.SecurityType;
                        contract.Symbol = arg.ContractDetails.Summary.Symbol;
                        contract.Exchange = arg.ContractDetails.Summary.Exchange;
                        contract.PrimaryExchange = arg.ContractDetails.Summary.PrimaryExchange;
                        contract.Currency = arg.ContractDetails.Summary.Currency;
                        IBContract ibContract = new IBContract
                        {
                            Contract = contract,
                            TradingHours = arg.ContractDetails.TradingHours,
                            ReqId = reqId
                        };
                        ibContract.MinTick = (decimal)arg.ContractDetails.MinTick;
                        ibContract.TimeZoneId = arg.ContractDetails.TimeZoneId;
                        ibContract.Multiplier = arg.ContractDetails.Summary.Multiplier;
                        tcs.TrySetResult((T)(object)ibContract);
                        /*
                        string[] ruleIds = arg.ContractDetails.MarketRuleIds.Split(new char[] { ',' });
                        if (parameter != null)
                        {
                            for (int i = 0; i < ruleIds.Length; i++)
                            {
                                ((IBController)parameter).Client.reqMarketRule(int.Parse(ruleIds[i]));
                            }
                        }
                          */
                    }
                }
                else if (args.GetType() == typeof(OrderStatusEventArgs))
                {
                    OrderStatusEventArgs arg = args as OrderStatusEventArgs;
                    // for CancelOrder
                    if (parameter != null && parameter.GetType() == typeof(int) && arg.OrderId == (int)parameter)
                    {
                        if (arg.Status == OrderStatus.ApiCancelled || arg.Status == OrderStatus.Canceled)
                            tcs.TrySetResult((T)(object)(true, OrderExecutionError.None));
                        else if (arg.Remaining == 0)
                            tcs.TrySetResult((T)(object)(false, OrderExecutionError.AlreadyFilled));
                        else
                            return; // wait
                    }

                    // for PlaceOrder
                    if (arg.OrderId == orderId)
                    {
                        if (arg.Status == OrderStatus.ApiCancelled || arg.Status == OrderStatus.Canceled)
                            tcs.TrySetResult((T)(object)(false, OrderExecutionError.Canceled));
                        else
                            tcs.TrySetResult((T)(object)(true, OrderExecutionError.None));
                    }
                }
                // for PlaceOrder
                else if (args.GetType() == typeof(OpenOrderEventArgs))
                {
                    OpenOrderEventArgs arg = args as OpenOrderEventArgs;
                    if (arg.OrderId == orderId)
                    {
                        if (arg.OrderState.Status == OrderStatus.Inactive || arg.OrderState.Status == OrderStatus.Canceled
                        || arg.OrderState.Status == OrderStatus.ApiCancelled)
                            tcs.TrySetResult((T)(object)(false, OrderExecutionError.None));
                        else
                            tcs.TrySetResult((T)(object)(true, OrderExecutionError.None));
                    }
                }
                // get min price increment for each exchange
                /*
                else if (args.GetType() == typeof(MarketRuleEventArgs))
                {
                    MarketRuleEventArgs arg = args as MarketRuleEventArgs;
                    List<object> list = new List<object>();
                    list.Add(contract);
                    list.Add(arg.PriceIncrements);
                    tcs.TrySetResult((T)(object)list);
                }*/
            };
            registerEvent(handler);

            try
            {
                using (token.Register(() => tcs.SetCanceled()))
                {
                    (orderId, prevOrderStatus, funcError) = func(reqId);
                    if (funcError == OrderExecutionError.None)
                        return await tcs.Task;
                    else
                        return (T)(object)(false, funcError);
                }
            }
            finally
            {
                unregisterEvent(handler);
            }
        }

        // for requesting NextValidId from IB servers
        public static async Task<TEventArgs> FromEventToAsync<TEventArgs>(
            Action<EventHandler<TEventArgs>> registerEvent,
            System.Action action,
            Action<EventHandler<TEventArgs>> unregisterEvent,
            CancellationToken token,
            System.Action beforeAction = null,
            System.Action afterAction = null
            )
        {
            beforeAction?.Invoke();
            var tcs = new TaskCompletionSource<TEventArgs>();
            EventHandler<TEventArgs> handler = (sender, args) => tcs.TrySetResult(args);
            registerEvent(handler);

            try
            {
                using (token.Register(() => tcs.SetCanceled()))
                {
                    action();
                    return await tcs.Task;
                }
            }
            finally
            {
                unregisterEvent(handler);
                afterAction?.Invoke();
            }
        }
    }
}
