using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Windows.Data;
using System.Windows.Controls;
using AmiBroker.OrderManager;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Specialized;
using System.Windows.Threading;
using System.Windows;
using Easy.MessageHub;
using System.IO;
using Krs.Ats.IBNet;

namespace AmiBroker.Controllers
{
    public class WeekDay
    {
        public string Name { get; set; }
        public int Value { get; set; }
    }
    public class TimeZone
    {
        public string Id { get; set; }
        [JsonIgnore]
        public TimeSpan UtcOffset { get
            {
                return TimeZoneInfo.GetUtcOffset(DateTime.Now);
            }
        }
        public string Description { get; set; }
        [JsonIgnore]
        public TimeZoneInfo TimeZoneInfo { get; set; }
        public override string ToString()
        {
            float offset = UtcOffset.Hours + UtcOffset.Minutes / 60;
            return "UTC" + (offset != 0 ? offset.ToString("+00;-00") : "") + "/" + Description;
        }
    }
    public class FilteredAccount
    {
        public string Id { get; set; }  // usually should be symbol + script's name
        public SymbolInAction Symbol { get; set; }

        private List<AccountInfo> _accounts;
        public List<AccountInfo> Accounts { get => _accounts ?? (_accounts = new List<AccountInfo>()); }
    }
    public class MainViewModel : NotifyPropertyChangedBase
    {
        private readonly MessageHub _hub = MessageHub.Instance;
        private string _pStatusMsg;
        public string StatusMsg
        {
            get { return _pStatusMsg; }
            set { _UpdateField(ref _pStatusMsg, value); }
        }
        // locks
        public readonly static object ordersLock = new object();
        public readonly static object updatedOrdersLock = new object();
        public readonly static object orderInfoListLock = new object();
        private readonly static object minorLogLock = new object();
        private readonly static object minorLogFileLock = new object();
        private readonly static object msgFileLock = new object();
        private readonly static object logFileLock = new object();
        private readonly static object log4FileLock = new object();
        //public readonly static object orderInfoLock = new object();    // orderInfo
        // Icons
        public Image ImageSaveLayout { get; private set; } = Util.MaterialIconToImage(MaterialIcons.ContentSaveAll, Util.Color.Indigo);
        public Image ImageRestoreLayout { get; private set; } = Util.MaterialIconToImage(MaterialIcons.WindowRestore, Util.Color.Indigo);
        public Image ImagePowerPlug { get; private set; } = Util.MaterialIconToImage(MaterialIcons.PowerPlug, Util.Color.Green);
        public Image ImagePowerPlugOff { get; private set; } = Util.MaterialIconToImage(MaterialIcons.PowerPlugOff, Util.Color.Red);
        public Image ImageSettings { get; private set; } = Util.MaterialIconToImage(MaterialIcons.Settings);
        public Image ImageHelp { get; private set; } = Util.MaterialIconToImage(MaterialIcons.BookOpenPageVariant, Util.Color.Purple);
        public Image ImageAbout { get; private set; } = Util.MaterialIconToImage(MaterialIcons.HelpCircle);
        public Image ImageRefresh { get; private set; } = Util.MaterialIconToImage(MaterialIcons.Refresh, Util.Color.Green);
        //public Image ImageOrderCancel { get; private set; } = new Image() { Source = new BitmapImage(new Uri("pack://application:,,,/OrderManager;component/Controllers/images/order-cancel.png")) };        
        // commands
        public Commands Commands { get; set; } = new Commands();
        public ObservableCollection<TimeZone> TimeZones { get; private set; } = new ObservableCollection<TimeZone>();
        public List<WeekDay> Weekdays { get; set; } = new List<WeekDay>();
        // All OrderTypes 
        public List<BaseOrderType> AllIBOrderTypes { get; set; } = new List<BaseOrderType>();
        public List<BaseOrderType> AllFTOrderTypes { get; set; } = new List<BaseOrderType>();
        public List<VendorOrderType> VendorOrderTypes { get; set; } = new List<VendorOrderType>();
        public UserPreference UserPreference { get; private set; }
        public ObservableCollection<IController> Controllers { get; set; }
        public Dictionary<string, OrderInfo> OrderInfoList { get; set; } = new Dictionary<string, OrderInfo>();
        // Lists for displaying
        public ObservableCollection<Message> MessageList { set; get; }

        public List<string> StopLossSource { set; get; } = new List<string>();
        public ObservableCollection<Log> LogList { set; get; }
        public ObservableCollectionEx<Log> MinorLogList { set; private get; } = new ObservableCollectionEx<Log>();
        public ObservableCollectionEx<DisplayedOrder> Orders { set; get; }

        // store updated order to prevent duplicating
        public ObservableCollection<DisplayedOrder> UpdatedOrders { set; get; } = new ObservableCollection<DisplayedOrder>();
        public ObservableCollection<SymbolInMkt> Portfolio { set; get; } = new ObservableCollection<SymbolInMkt>();
        public SymbolInMkt SelectedPortfolio { get; set; }
        public ObservableCollection<SymbolInAction> SymbolInActions { get; set; }
        public ICollectionView PendingOrdersView { get; set; }
        public DisplayedOrder SelectedPendingOrder { get; set; }
        public ICollectionView ExecutionView { get; set; }
        // collectionViewSources for views
        private CollectionViewSource poViewSource;
        private CollectionViewSource execViewSource;
        private Dictionary<string, string> AccountAlias = new Dictionary<string, string>();
        // for script treeview use -- selected treeview item
        private object _pSelectedItem;
        public object SelectedItem
        {
            get { return _pSelectedItem; }
            set {
                _UpdateField(ref _pSelectedItem, value);
                Dummy = !Dummy;
            }
        }

        // for binding(displaying) controller for each symbol
        private bool _pDummy;
        public bool Dummy
        {
            get { return _pDummy; }
            set { _UpdateField(ref _pDummy, value); }
        }

        // for selecting accounts - multiselect-combox
        public CustomFilterService FilterService { get; } = new CustomFilterService();

        public static List<OrderStatus> IncompleteStatus { get; } = new List<OrderStatus> { OrderStatus.Inactive,
            OrderStatus.PendingSubmit, OrderStatus.Submitted, OrderStatus.PartiallyFilled, OrderStatus.PreSubmitted };
        public static IEnumerable<OrderInfo> GetUnfilledOrderInfo(IEnumerable<OrderInfo> orderInfos)
        {
            List<OrderInfo> infos = orderInfos.ToList();
            OrderInfo info = infos.Where(x => x.OrderStatus == null ||
                (x.OrderStatus != null && IncompleteStatus.Any(y => y == x.OrderStatus?.Status) && x.OrderStatus.Remaining > 0)).LastOrDefault();

            if (info != null)
                return infos.Where(x => x.BatchNo == info.BatchNo && (x.OrderStatus == null ||
                                            IncompleteStatus.Any(y => y == x.OrderStatus?.Status)));
            else
                return null;
            
        }
        // singelton pattern
        public static MainViewModel Instance { get { return instance; } }
        private static readonly MainViewModel instance = new MainViewModel();
        // Explicit static constructor to tell C# compiler
        // not to mark type as beforefieldinit
        static MainViewModel()
        {
        }

        private string logfile = string.Empty;
        private string minorlogfile = string.Empty;
        private string orderfile = string.Empty;
        private string msgfile = string.Empty;
        private MainViewModel()
        {
            Controllers = new ObservableCollection<IController>();
            MessageList = new ObservableCollection<Message>();
            LogList = new ObservableCollection<Log>();

            Orders = new ObservableCollectionEx<DisplayedOrder>();
            Orders.CollectionChanged += Orders_CollectionChanged;
            ((INotifyPropertyChanged)Orders).PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Status")
                {
                    poViewSource.View.Refresh();
                    execViewSource.View.Refresh();
                }
            };            

            SymbolInActions = new ObservableCollection<SymbolInAction>();

            poViewSource = new CollectionViewSource();
            poViewSource.Source = Orders;
            poViewSource.Filter += PendingOrders_Filter;
            PendingOrdersView = poViewSource.View;

            execViewSource = new CollectionViewSource();
            execViewSource.Source = Orders;
            execViewSource.Filter += Execution_Filter;
            ExecutionView = execViewSource.View;
                        
            // retrieving the settings
            ReadSettings();
            ReadAccountAlias();

            // reading all order types
            var types = typeof(IBOrderType).Assembly.GetTypes().Where(x => x.IsSubclassOf(typeof(IBOrderType)));
            foreach (var t in types)
            {
                AllIBOrderTypes.Add((IBOrderType)Activator.CreateInstance(t));
            }
            types = typeof(FTOrderType).Assembly.GetTypes().Where(x => x.IsSubclassOf(typeof(FTOrderType)));
            foreach (var t in types)
            {
                AllFTOrderTypes.Add((FTOrderType)Activator.CreateInstance(t));
            }
            //VendorOrderTypes.Add("Order Types for IB", AllIBOrderTypes);
            //VendorOrderTypes.Add("Order Types for FT", AllFTOrderTypes);
            VendorOrderTypes.Add(new VendorOrderType { Name = "Order Types for Interative Broker", OrderTypes = AllIBOrderTypes });
            VendorOrderTypes.Add(new VendorOrderType { Name = "Order Types for FuTu NiuNiu", OrderTypes = AllFTOrderTypes });

            
            TimeZones.Add(new TimeZone { Id = "HKT", TimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"), Description = "China Standar Time" });
            TimeZones.Add(new TimeZone { Id = "EST", TimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"), Description = "Eastern Standard Time (North America)" });
            //TimeZones.Add(new TimeZone { Id = "CDT", TimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Central Daylight Time"), Description = "Eastern Standard Time (North America)" });

            Weekdays.Add(new WeekDay { Value = 0, Name = "Sun" });
            Weekdays.Add(new WeekDay { Value = 1, Name = "Mon" });
            Weekdays.Add(new WeekDay { Value = 2, Name = "Tue" });
            Weekdays.Add(new WeekDay { Value = 3, Name = "Wed" });
            Weekdays.Add(new WeekDay { Value = 4, Name = "Thu" });
            Weekdays.Add(new WeekDay { Value = 5, Name = "Fri" });
            Weekdays.Add(new WeekDay { Value = 6, Name = "Sat" });

            StopLossSource.Add("In Points/Dollars");
            StopLossSource.Add("In Ticks");
            StopLossSource.Add("In Percentage");
            StopLossSource.Add("From AFL script");
            //
            _hub.Subscribe<IController>(OnControllerConnected);
        }
        private void OnControllerConnected(IController controller)
        {
            if (controller.IsConnected)
            {
                foreach (SymbolInAction symbol in SymbolInActions)
                {
                    symbol.FillInSymbolDefinition(controller);
                    symbol.FillInContractDetails(controller);
                }
            }
        }
        
        public static List<int> GetOrderIds(OrderInfo oi)
        {
            List<int> result = new List<int>();
            IEnumerable<OrderInfo> orderInfos = oi.Strategy.AccountStat[oi.Account.Name].OrderInfos[oi.OrderAction]
                    .Where(x => x.BatchNo == oi.BatchNo);
            foreach (var orderInfo in orderInfos)
            {
                result.Add(orderInfo.RealOrderId);
            }
            return result;
        } 
        public void RemoveSymbol(SymbolInAction symbol)
        {
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                SymbolInActions.Remove(symbol);
            });
        }
        public bool AddSymbol(string name, float timeframe, out SymbolInAction symbol)
        {
            symbol = SymbolInActions.FirstOrDefault(x => x.Name == name && x.TimeFrame == timeframe);
            if (symbol == null || (symbol != null && symbol.IsDirty))
            {
                if (symbol != null)
                {
                    symbol.RefreshScripts();
                    symbol.IsDirty = false;
                }
                else
                {
                    symbol = new SymbolInAction(name, timeframe);
                    SymbolInAction tmp = symbol;
                    Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                    {
                        SymbolInActions.Add(tmp);
                    });
                    foreach (IController controller in Controllers)
                    {
                        if (controller.IsConnected)
                        {
                            symbol.FillInSymbolDefinition(controller);
                            symbol.FillInContractDetails(controller);
                        }                        
                    }                    
                }                
                return true;
            }
            return false;
        }
        public void AddOrderInfo(string key, OrderInfo oi)
        {
            lock (orderInfoListLock)
            {
                Instance.OrderInfoList.Add(key, oi);
            }
        }

        public void Log4(string filePath, string text, bool isAppend = true)
        {            
            try
            {
                if (!File.Exists(filePath))
                {
                    FileStream fs = File.Create(filePath);
                    fs.Close();
                }
                //lock (log4FileLock)
                {
                    using (var sw = new StreamWriter(filePath, isAppend))
                    {
                        sw.WriteLine(text);
                    }
                }
            }
            catch (Exception ex)
            {
                GlobalExceptionHandler.HandleException("Log", ex);
            }
        }
        public void Log(Log log)
        {
            Dispatcher.FromThread(OrderManager.UIThread).InvokeAsync(() =>
            {
                if (LogList.Count > 0 && log.Text == LogList[0].Text && log.Source == LogList[0].Source)
                    LogList[0].Time = log.Time;
                else
                {
                    log.Text = log.Text.TrimEnd('\n');
                    LogList.Insert(0, log);                    
                    if (File.Exists(logfile))
                    {
                        try
                        {
                            lock (logFileLock)
                            {
                                using (var sw = new StreamWriter(logfile, true))
                                {
                                    sw.WriteLine(ListViewHelper.ObjectToLine(log));
                                }
                            }                            
                        }
                        catch (Exception ex)
                        {
                            GlobalExceptionHandler.HandleException("Log", ex);
                        }
                        
                    }
                }
                    
            });
        }
        public void MinorLog(Log log)
        {
            bool errFilterFound = false;

            if (UserPreference != null && !string.IsNullOrEmpty(UserPreference.ErrorFilter))
            {
                string[] filters = UserPreference.ErrorFilter.Split(new char[] { ';' });
                for (int i = 0; i < filters.Length; i++)
                {
                    if (!string.IsNullOrEmpty(filters[i]) && log.Text.ToLower().Contains(filters[i].ToLower()))
                    {
                        errFilterFound = true;
                        return;
                    }
                }
            }

            bool msgAllowFound = false;
            if (UserPreference != null && !string.IsNullOrEmpty(UserPreference.LogAllowDuplicated))
            {
                string[] filters = UserPreference.LogAllowDuplicated.Split(new char[] { ';' });
                for (int i = 0; i < filters.Length; i++)
                {
                    if (!string.IsNullOrEmpty(filters[i]) && log.Text.ToLower().Contains(filters[i].ToLower()))
                    {
                        msgAllowFound = true;
                        break;
                    }
                }
            }

            bool isAdded = false;
            bool duplicatedFound = false;
            lock (minorLogLock)
            {
                if (!msgAllowFound)
                {
                    // in case of duplicated found, only time will be changed, no item will be added
                    Log l = MinorLogList.FirstOrDefault(x => x.Source == log.Source);
                    if (l != null && l.Text == log.Text && !OrderManager.MainWin.MinorLogPause)
                    {
                        //l.Time = log.Time;
                        Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                        {
                            //lock (minorLogLock)
                            {
                                MinorLogList.Remove(l);
                                MinorLogList.Insert(0, log);
                            }
                        });
                        duplicatedFound = true;
                    }
                }                

                if (!OrderManager.MainWin.MinorLogPause && !duplicatedFound)
                {
                    Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                    {
                        //lock (minorLogLock)
                        {
                            MinorLogList.Insert(0, log);                            
                        }
                    });
                    isAdded = true;
                }
                    
            }            

            if (isAdded && File.Exists(minorlogfile))
            {
                try
                {
                    lock (minorLogFileLock)
                    {
                        using (var sw = new StreamWriter(minorlogfile, true))
                        {
                            sw.WriteLine(ListViewHelper.ObjectToLine(log));
                        }
                    }                    
                }
                catch (Exception ex)
                {
                    GlobalExceptionHandler.HandleException("Minor Log", ex);
                }
            }
        }
        public void AddMessage(Message msg)
        {
            Dispatcher.FromThread(OrderManager.UIThread).InvokeAsync(() =>
            {
                MessageList.Insert(0, msg);
                CreateLoggingFiles();
                if (File.Exists(msgfile))
                {
                    try
                    {
                        lock (msgFileLock)
                        {
                            using (var sw = new StreamWriter(msgfile, true))
                            {
                                sw.WriteLine(ListViewHelper.ObjectToLine(msg));
                            }
                        }                        
                    }
                    catch (Exception ex)
                    {
                        GlobalExceptionHandler.HandleException("Message Logging", ex);
                    }
                    
                }
            });
        }

        public void InsertUpdatedOrder(DisplayedOrder dOrder)
        {
            lock (updatedOrdersLock)
            {
                UpdatedOrders.Add(dOrder);
                MinorLog(new Log
                {
                    Text = string.Format("OrderId:{0}, status:{1}, filled:{2}, remaining:{3}",
                    dOrder.OrderId, dOrder.Status, dOrder.Filled, dOrder.Remaining),
                    Source = "Insert UpdatedOrder",
                    Time = DateTime.Now
                });
            }
        }

        public void InsertOrder(DisplayedOrder order)
        {
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                lock (ordersLock)
                {
                    Orders.Insert(0, order);
                }
                
                //CreateLoggingFiles();
                if (File.Exists(orderfile))
                {
                    try
                    {
                        using (var sw = new StreamWriter(orderfile, true))
                        {
                            sw.WriteLine(ListViewHelper.ObjectToLine(order));
                        }
                    }
                    catch (Exception ex)
                    {
                        GlobalExceptionHandler.HandleException("Order Log", ex);
                   }
                    
                }
            });
        }
        
        private void Orders_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            PendingOrdersView.Refresh();
            ExecutionView.Refresh();

        }

        private void CreateLoggingFiles()
        {
            try
            {
                if (UserPreference.AutoLoggingEnabled && !string.IsNullOrEmpty(UserPreference.LoggingPath))
                {
                    if (string.IsNullOrEmpty(logfile))
                    {
                        logfile = Path.Combine(UserPreference.LoggingPath, "log" + DateTime.Now.ToString("yyyyMMddHHmm") + ".csv");
                        FileStream fs = File.Create(logfile);
                        fs.Close();
                        using(var sw = new StreamWriter(logfile, true))
                        {
                            Log o = new Log();
                            sw.WriteLine(ListViewHelper.ObjectToHeader(o));
                        }
                        minorlogfile = Path.Combine(UserPreference.LoggingPath, "minor" + DateTime.Now.ToString("yyyyMMddHHmm") + ".csv");
                        fs = File.Create(minorlogfile);
                        fs.Close();
                        using (var sw = new StreamWriter(minorlogfile, true))
                        {
                            Log o = new Log();
                            sw.WriteLine(ListViewHelper.ObjectToHeader(o));
                        }
                        orderfile = Path.Combine(UserPreference.LoggingPath, "order" + DateTime.Now.ToString("yyyyMMddHHmm") + ".csv");
                        fs = File.Create(orderfile);
                        fs.Close();
                        using (var sw = new StreamWriter(orderfile, true))
                        {
                            DisplayedOrder o = new DisplayedOrder();
                            sw.WriteLine(ListViewHelper.ObjectToHeader(o));
                        }
                        msgfile = Path.Combine(UserPreference.LoggingPath, "message" + DateTime.Now.ToString("yyyyMMddHHmm") + ".csv");
                        fs = File.Create(msgfile);
                        fs.Close();
                        using (var sw = new StreamWriter(msgfile, true))
                        {
                            Message o = new Message();
                            sw.WriteLine(ListViewHelper.ObjectToHeader(o));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MinorLog(new Log
                {
                    Text = ex.Message,
                    Source = "Creating log files",
                    Time = DateTime.Now
                });
            }
            
        }

        // filters for CollectionView
        private List<string> pendingStatus = new List<string>() { "Inactive", "PreSumitted", "Submitted", "Pending" };
        private void PendingOrders_Filter(object sender, FilterEventArgs e)
        {
            var order = e.Item as DisplayedOrder;
            e.Accepted = pendingStatus.Any(s => order.Status.ToString().Contains(s));
        }

        private void Execution_Filter(object sender, FilterEventArgs e)
        {
            var order = e.Item as DisplayedOrder;
            e.Accepted = !pendingStatus.Any(s => order.Status.ToString().Contains(s));
        }

        private void ReadAccountAlias()
        {
            Dictionary<string, string> alias = JsonConvert.DeserializeObject<Dictionary<string, string>>(Properties.Settings.Default["AccountAlias"].ToString());
            if (alias != null)
                AccountAlias = alias;
        }

        private void UpdateAccountAlias(string accountName, string alias)
        {
            AccountInfo accountInfo = Controllers.FirstOrDefault(x => x.Accounts.Any(y => y.Name == accountName))?
                .Accounts.FirstOrDefault(x => x.Name == accountName);
            if (accountInfo != null)
                accountInfo.Alias = alias;
        }

        public void SaveAccountAlias(string accountName, string alias)
        {
            if (AccountAlias.ContainsKey(accountName))
            {
                if (string.IsNullOrEmpty(alias))
                    AccountAlias.Remove(accountName);
                else
                    AccountAlias[accountName] = alias;
            }
            else
            {
                if (!string.IsNullOrEmpty(alias))
                    AccountAlias.Add(accountName, alias);
            }
            // will update in Converter
            //UpdateAccountAlias(accountName, alias);
            Properties.Settings.Default["AccountAlias"] = JsonConvert.SerializeObject(AccountAlias);
            Properties.Settings.Default.Save();
        }

        public string FindAccountAlias(string name)
        {
            if (AccountAlias.ContainsKey(name))
                return AccountAlias[name];
            else
                return string.Empty;
        }

        public void ReadSettings()
        {
            UserPreference = JsonConvert.DeserializeObject<UserPreference>(Properties.Settings.Default["preference"].ToString());
            List<IController> all_ctrls = new List<IController>();
            if (UserPreference != null)
            {
                Type t = typeof(Helper);
                string ns = t.Namespace;
                foreach (string vendor in UserPreference.Vendors)
                {
                    AccountOption accOpt = (dynamic)UserPreference.GetType().GetProperty(vendor + "Account").GetValue(UserPreference);
                    foreach (var acc in accOpt.Accounts)
                    {
                        if (acc.IsActivate)
                        {
                            string clsName = ns + "." + vendor + "Controller";
                            Type type = Type.GetType(clsName);
                            IController ctrl = Activator.CreateInstance(type, this) as IController;
                            ctrl.ConnParam = acc;
                            // if some connection is connected, then remain unchanged
                            IController ic = Controllers.FirstOrDefault(x => x.DisplayName == ctrl.DisplayName);
                            if (ic != null)
                            {
                                ic.ConnParam = acc;
                                all_ctrls.Add(ic);
                            }
                            else
                            {
                                all_ctrls.Add(ctrl);
                            }                                
                        }
                    }
                    // add new controllers
                    foreach (var ctrl in all_ctrls)
                    {
                        IController ic = Controllers.FirstOrDefault(x => x.DisplayName == ctrl.DisplayName);
                        if (ic == null)
                            Controllers.Add(ctrl);
                    }

                    // remove out-of-date controllers
                    foreach (var ctrl in Controllers.ToList())
                    {
                        IController ic = all_ctrls.FirstOrDefault(x => x.DisplayName == ctrl.DisplayName);
                        if (ic == null)
                        {
                            foreach (var symbol in SymbolInActions)
                            {
                                symbol.AppliedControllers.Remove(ctrl);
                            }
                            Controllers.Remove(ctrl);
                        }
                    }
                    
                }
            }

        }
    }
}
