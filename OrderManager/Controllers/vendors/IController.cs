﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Sdl.MultiSelectComboBox.API;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Collections.Specialized;
using System.Runtime.Serialization;
using AmiBroker.OrderManager;
using Newtonsoft.Json;
using Krs.Ats.IBNet;

namespace AmiBroker.Controllers
{
    public enum AccountStatus 
    {
        None=1,
        BuyPending=2,
        Long=4,
        ShortPending=8,
        Short=16,
        SellPending=32,
        CoverPending=64,
        APSLongActivated=128, // adaptive profit stop activated
        APSShortActivated = 256, // adaptive profit stop activated
        StoplossLongActivated = 512,
        StoplossShortActivated = 1024,
        PreForceExitLongActivated = 2048,
        PreForceExitShortActivated = 4096,
        FinalForceExitLongActivated = 8192,
        FinalForceExitShortActivated = 16384,
        /*
        BuyPartiallyFilled = 32768,
        ShortPartiallyFilled = 65536,
        SellPartiallyFilled = 131072,
        CoverPartiallyFilled = 262144,
        APSLongPartiallyFilled = 524288,
        APSShortPartiallyFilled = 1048576,
        StoplossLongPartiallyFilled = 2097152,
        StoplossShortPartiallyFilled = 4194304,*/
    }

    public enum OrderExecutionError
    {
        None = 0,
        InsufficientEquity = 1,
        ExchangeClosed = 2,
        OrderCanceled = 3,
        AlreadyCanceled = 4,
        PendingCancel = 5,
        IdNotFound = 6,
        DuplicateOrderId = 103,
        CannotModifyFilledOrder = 8,
        CannotCancelFilledOrder = 9,
        Unknown = 10,
        NotConnected = 11,
        ConnectionError = 12,
        OrderSizeError = 13, // be zero or negative
        EmptyErrorMsg = 14,
        Exception = 15,
        AlreadyFilled = 16,
        StopPriceRevisionDisallowed = 17,
        Canceled = 18,
    }

    public enum BrokerConnectionStatus
    {
        Connected = 1,
        Connecting = 2,
        Disconnected = 3,
        Error = 4
    }

    public class AccountStatusOp
    {
        //private readonly static List<string> PendingStatus = ["PreSubmitted"];
        private readonly static OrderAction[] ShortExitAction = { OrderAction.APSShort, OrderAction.FinalForceExitShort,
            OrderAction.Cover, OrderAction.PreForceExitShort, OrderAction.StoplossShort };
        private readonly static OrderAction[] LongExitAction = { OrderAction.APSLong, OrderAction.FinalForceExitLong,
            OrderAction.Sell, OrderAction.PreForceExitLong, OrderAction.StoplossLong };

        public static void RevertActionStatus(BaseStat strategyStat, BaseStat scriptStat, Strategy strategy, OrderAction orderAction, int batchNo, bool cancelled = false)
        {
            string strategyName = strategy.Name;
            strategy.StatusChanged = true; 
            if (orderAction == OrderAction.Buy)
            {                
                if ((strategyStat.AccountStatus & AccountStatus.BuyPending) != 0 && strategyStat.LongPosition > 0
                    && OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus |= AccountStatus.Long;
                    strategyStat.AccountStatus &= ~AccountStatus.BuyPending;
                    scriptStat.LongStrategies.Add(strategyName);
                    scriptStat.LongPendingStrategies.Remove(strategyName);
                }
                if ((strategyStat.AccountStatus & AccountStatus.BuyPending) != 0 && strategyStat.LongPosition == 0
                    && OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.BuyPending;
                    scriptStat.LongPendingStrategies.Remove(strategyName);
                }    
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategy.ResetLongActionAfterParam(OrderAction.Buy);
                }
            }
            else if (orderAction == OrderAction.Short)
            {
                if ((strategyStat.AccountStatus & AccountStatus.ShortPending) != 0 && strategyStat.ShortPosition > 0
                    && OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus |= AccountStatus.Short;
                    strategyStat.AccountStatus &= ~AccountStatus.ShortPending;
                    scriptStat.ShortStrategies.Add(strategyName);
                    scriptStat.ShortPendingStrategies.Remove(strategyName);
                }
                if ((strategyStat.AccountStatus & AccountStatus.ShortPending) != 0 && strategyStat.ShortPosition == 0
                    && OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.ShortPending;
                    scriptStat.ShortPendingStrategies.Remove(strategyName);
                }
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategy.ResetShortActionAfterParam(OrderAction.Short);
                }
            }
            else if (orderAction == OrderAction.Sell)
            {                
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.SellPending;
                    strategy.ResetLongActionAfterParam(OrderAction.Sell);
                }
            }
            else if (orderAction == OrderAction.Cover)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.CoverPending;
                    strategy.ResetShortActionAfterParam(OrderAction.Cover);
                }
            }
            else if (orderAction == OrderAction.APSLong)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategy.ResetLongActionAfterParam(OrderAction.APSLong);
                    strategyStat.AccountStatus &= ~AccountStatus.APSLongActivated;
                }
            }
            else if (orderAction == OrderAction.APSShort)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategy.ResetShortActionAfterParam(OrderAction.APSShort);
                    strategyStat.AccountStatus &= ~AccountStatus.APSShortActivated;
                }
            }
            else if (orderAction == OrderAction.StoplossLong)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategy.ResetLongActionAfterParam(OrderAction.StoplossLong);
                    strategyStat.AccountStatus &= ~AccountStatus.StoplossLongActivated;
                }
            }
            else if (orderAction == OrderAction.StoplossShort)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategy.ResetShortActionAfterParam(OrderAction.StoplossShort);
                    strategyStat.AccountStatus &= ~AccountStatus.StoplossShortActivated;
                }
            }
            else if (orderAction == OrderAction.PreForceExitLong)
            {
                strategyStat.AccountStatus &= ~AccountStatus.PreForceExitLongActivated;
            }
            else if (orderAction == OrderAction.PreForceExitShort)
            {
                strategyStat.AccountStatus &= ~AccountStatus.PreForceExitShortActivated;
            }
            else if (orderAction == OrderAction.FinalForceExitLong)
            {
                strategyStat.AccountStatus &= ~AccountStatus.FinalForceExitLongActivated;
            }
            else if (orderAction == OrderAction.FinalForceExitShort)
            {
                strategyStat.AccountStatus &= ~AccountStatus.FinalForceExitShortActivated;
            }
            
        }

        // set initial status of OrderAction
        public static void SetActionInitStatus(BaseStat strategyStat, BaseStat scriptStat, Strategy strategy, OrderAction orderAction)
        {
            string strategyName = strategy.Name;
            strategy.StatusChanged = true;
            if (orderAction == OrderAction.Buy)
            {
                strategyStat.AccountStatus |= AccountStatus.BuyPending;
                scriptStat.LongPendingStrategies.Add(strategyName);
                strategy.ResetLongActionAfterParam(OrderAction.Buy);
            }
            else if (orderAction == OrderAction.Short)
            {
                strategyStat.AccountStatus |= AccountStatus.ShortPending;
                scriptStat.ShortPendingStrategies.Add(strategyName);
                strategy.ResetShortActionAfterParam(OrderAction.Short);
            }
            else if (orderAction == OrderAction.Sell)
            {
                strategyStat.AccountStatus |= AccountStatus.SellPending;
                strategy.ResetLongActionAfterParam(OrderAction.Sell);
            }
            else if (orderAction == OrderAction.Cover)
            {
                strategyStat.AccountStatus |= AccountStatus.CoverPending;
                strategy.ResetShortActionAfterParam(OrderAction.Cover);
            }
            else if (orderAction == OrderAction.APSLong)
            {
                strategyStat.AccountStatus |= AccountStatus.APSLongActivated;
                strategy.ResetLongActionAfterParam(OrderAction.APSLong);
            }
            else if (orderAction == OrderAction.APSShort)
            {
                strategyStat.AccountStatus |= AccountStatus.APSShortActivated;
                strategy.ResetShortActionAfterParam(OrderAction.APSShort);
            }
            else if (orderAction == OrderAction.StoplossLong)
            {
                strategyStat.AccountStatus |= AccountStatus.StoplossLongActivated;
                strategy.ResetLongActionAfterParam(OrderAction.StoplossLong);
            }
            else if (orderAction == OrderAction.StoplossShort)
            {
                strategyStat.AccountStatus |= AccountStatus.StoplossShortActivated;
                strategy.ResetShortActionAfterParam(OrderAction.StoplossShort);
            }
            else if (orderAction == OrderAction.PreForceExitLong)
            {
                strategyStat.AccountStatus |= AccountStatus.PreForceExitLongActivated;
            }
            else if (orderAction == OrderAction.PreForceExitShort)
            {
                strategyStat.AccountStatus |= AccountStatus.PreForceExitShortActivated;
            }
            else if (orderAction == OrderAction.FinalForceExitLong)
            {
                strategyStat.AccountStatus |= AccountStatus.FinalForceExitLongActivated;
            }
            else if (orderAction == OrderAction.FinalForceExitShort)
            {
                strategyStat.AccountStatus |= AccountStatus.FinalForceExitShortActivated;
            }
        }

        // batchNO = -1 ONLY occurs under manually assignment of existing positions
        public static void SetPositionStatus(BaseStat strategyStat, BaseStat scriptStat, Strategy strategy, OrderAction orderAction, int batchNo = -1, OrderInfo oi = null)
        {
            strategy.StatusChanged = true;
            if (orderAction == OrderAction.Buy)
            {
                if (batchNo == -1)
                {
                    strategyStat.AccountStatus |= AccountStatus.Long;
                }
                else 
                {
                    if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                    {
                        strategyStat.AccountStatus &= ~AccountStatus.BuyPending;
                        strategyStat.AccountStatus |= AccountStatus.Long;
                        scriptStat.LongPendingStrategies.Remove(strategy.Name);
                        if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].Filled > 0)
                        {                            
                            scriptStat.LongStrategies.Add(strategy.Name);
                        }
                    }                        
                }                
            }
            else if (orderAction == OrderAction.Short)
            {
                if (batchNo == -1)
                {
                    strategyStat.AccountStatus |= AccountStatus.Short;
                }
                else
                {
                    if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                    {
                        strategyStat.AccountStatus &= ~AccountStatus.ShortPending;
                        strategyStat.AccountStatus |= AccountStatus.Short;
                        scriptStat.ShortPendingStrategies.Remove(strategy.Name);
                        if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].Filled > 0)
                        {                            
                            scriptStat.ShortStrategies.Add(strategy.Name);
                        }
                    }
                }
            }
            else if (orderAction == OrderAction.Sell)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.SellPending;
                }
            }
            else if (orderAction == OrderAction.Cover)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.CoverPending;
                }
            }
            else if (orderAction == OrderAction.APSLong)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.APSLongActivated;
                }
            }
            else if (orderAction == OrderAction.APSShort)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.APSShortActivated;
                }
            }
            else if (orderAction == OrderAction.StoplossLong)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.StoplossLongActivated;
                }
            }
            else if (orderAction == OrderAction.StoplossShort)
            {
                if (OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
                {
                    strategyStat.AccountStatus &= ~AccountStatus.StoplossShortActivated;
                }
            }
            else if (orderAction == OrderAction.PreForceExitLong &&
                OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
            {
                strategyStat.AccountStatus &= ~AccountStatus.PreForceExitLongActivated;
            }
            else if (orderAction == OrderAction.PreForceExitShort &&
                OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
            {
                strategyStat.AccountStatus &= ~AccountStatus.PreForceExitShortActivated;
            }
            else if (orderAction == OrderAction.FinalForceExitLong &&
                OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
            {
                strategyStat.AccountStatus &= ~AccountStatus.FinalForceExitLongActivated;
            }
            else if (orderAction == OrderAction.FinalForceExitShort &&
                OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
            {
                strategyStat.AccountStatus &= ~AccountStatus.FinalForceExitShortActivated;                
            }

            string msg = string.Empty;
            if (strategyStat.LongPosition == 0 && LongExitAction.Contains(orderAction) &&
                OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
            {
                /*
                strategyStat.OrderInfos[OrderAction.APSLong].Clear();
                strategyStat.OrderInfos[OrderAction.StoplossLong].Clear();
                strategyStat.OrderInfos[OrderAction.Buy].Clear();
                strategyStat.OrderInfos[OrderAction.Sell].Clear();
                strategyStat.OrderInfos[OrderAction.PreForceExitLong].Clear();
                strategyStat.OrderInfos[OrderAction.FinalForceExitLong].Clear();*/
                scriptStat.LongStrategies.Remove(strategy.Name);

                strategyStat.AccountStatus &= ~AccountStatus.Long;
                strategyStat.AccountStatus &= ~AccountStatus.BuyPending;
                strategy.ForceExitOrderForLong.Reset();
                strategy.AdaptiveProfitStopforLong.Reset();
                strategy.ResetLongActionAfterParam();

                msg = "Long cleared, OrderAction:" + orderAction.ToString();
            }

            if (strategyStat.ShortPosition == 0 && ShortExitAction.Contains(orderAction) &&
                OrderManager.BatchPosSize[strategyStat.Account.Name + batchNo].IsCompleted)
            {
                /*
                strategyStat.OrderInfos[OrderAction.APSShort].Clear();
                strategyStat.OrderInfos[OrderAction.StoplossShort].Clear();
                strategyStat.OrderInfos[OrderAction.Short].Clear();
                strategyStat.OrderInfos[OrderAction.Cover].Clear();
                strategyStat.OrderInfos[OrderAction.PreForceExitShort].Clear();
                strategyStat.OrderInfos[OrderAction.FinalForceExitShort].Clear();*/
                scriptStat.ShortStrategies.Remove(strategy.Name);

                strategyStat.AccountStatus &= ~AccountStatus.Short;
                strategyStat.AccountStatus &= ~AccountStatus.ShortPending;
                strategy.ForceExitOrderForShort.Reset();
                strategy.AdaptiveProfitStopforShort.Reset();
                strategy.ResetShortActionAfterParam();

                msg = "Short cleared, OrderAction:" + orderAction.ToString();
            }

            if (!string.IsNullOrEmpty(msg))
            {
                MainViewModel.Instance.Log(new Log
                {
                    Text = msg,
                    Time = DateTime.Now,
                    Source = strategy.Symbol.Name + "." + strategy.Name
                });
            }
        }
        public static void SetAttemps(ref BaseStat strategyStat, OrderAction orderAction)
        {
            if (orderAction == OrderAction.Buy)
            {
                strategyStat.LongAttemps++;
            }
            if (orderAction == OrderAction.Short)
            {
                strategyStat.ShortAttemps++;
            }
        }
    }
    public class AccountTag : INotifyPropertyChanged
    {
        public Type Type { get; }
           
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }

        public string Tag { get; set; }
        public string Currency { get; set; }
        
        private string _pValue;
        public string Value
        {
            get { return _pValue; }
            set
            {
                if (_pValue != value)
                {
                    _pValue = value;
                    OnPropertyChanged("Value");
                }
            }
        }

    }
    
    public class AccountInfo : IItemGroupAware, IItemEnabledAware, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }        
        public string Name { get; private set; }        
        public IController Controller { get; private set; }
        [JsonIgnore]
        public ObservableCollection<AccountTag> Properties { get; set; } = new ObservableCollection<AccountTag>();
        // properties for use in Multi-select combox
        public IItemGroup Group { get; set; }
        public BitmapImage Image { get; private set; }
        public Size ImageSize { get; private set; }
        public bool IsEnabled { get; set; } = true;

        private AccountTag _pTotalCashValue;
        public AccountTag TotalCashValue
        {
            get { return _pTotalCashValue; }
            set
            {
                if (_pTotalCashValue != value)
                {
                    _pTotalCashValue = value;
                    OnPropertyChanged("TotalCashValue");
                }
            }
        }
                
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Alias))
                    return Alias;
                else
                    return Name;
            }
        }

        private string _pAlias;
        public string Alias
        {
            get { return _pAlias; }
            set
            {
                if (_pAlias != value)
                {
                    _pAlias = value;
                    OnPropertyChanged("DisplayName");
                    OnPropertyChanged("Alias");
                }
            }
        }

        //
        public AccountInfo(string name, IController controller)
        {
            Name = name;
            Controller = controller;
            string vendor = controller.Vendor;
            Uri uri = new Uri("pack://application:,,,/OrderManager;component/Controllers/images/"+vendor+".png");
            Image = new BitmapImage(uri);
            ImageSize = new Size(16, 16);
            Group = DefaultGroupService.GetItemGroup(vendor);
            Properties.CollectionChanged += Properties_CollectionChanged;
            Alias = MainViewModel.Instance.FindAccountAlias(name);
        }

        private void Properties_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                foreach (AccountTag tag in e.NewItems)
                {
                    if (tag.Tag.ToLower() == "totalcashvalue")
                    {
                        TotalCashValue = tag;
                    }
                }
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /*
    public class OrderIdCounter
    {
        public static object LockObject { get; } = new object();

        private static int _orderId = 0;
        public static int OrderId {
            get { return _orderId++; }
            set { _orderId = value; }
        }
    }*/
    public interface IController : IItemGroupAware, IItemEnabledAware
    {
        // IB can have more than one linked account (Finacial Advisor Account and sub accounts)
        ObservableCollection<AccountInfo> Accounts { get; }
        AccountInfo SelectedAccount { get; set; }
        // should be unique
        string DisplayName { get; }
        string Vendor { get; }  //short name
        string VendorFullName { get; }
        ConnectionParam ConnParam { get; set; }
        bool IsConnected { get; }
        BrokerConnectionStatus ConnectionStatus { get; }
        void Connect();
        void Disconnect();

        // properties for use in Multi-select combox
        BitmapImage Image { get; }
        Size ImageSize { get; }
        bool Dummy { get; set; }    // used in listview in account selecting section
        bool ModifyAsMarketOrder(IEnumerable<OrderInfo> oi);  // Modify order as Market Order

        // modify prices and quantity
        bool ModifyOrder(AccountInfo accountInfo, Strategy strategy, OrderAction orderAction, BaseOrderType orderType);
        Task<List<OrderLog>> PlaceOrder(AccountInfo accountInfo, Strategy strategy, BaseOrderType orderType, 
            OrderAction orderAction, int batchNo, double? posSize = null, Contract security = null, 
            bool errorSuppress = false, bool addToInfoList = true);
        void CancelOrder(int orderId);
        bool CancelOrders(OrderInfo orderInfo);
        Task<(bool, OrderExecutionError)> CancelOrderAsync(int orderId);
        Task<bool> CancelOrdersAsync(OrderInfo orderInfo);
        Task<(int, OrderExecutionError)> PlaceOrderAsync(Contract contract, Order order);
    }
}
