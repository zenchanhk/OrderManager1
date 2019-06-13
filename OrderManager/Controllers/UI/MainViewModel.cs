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
        public TimeSpan UtcOffset { get; set; }
        public string Description { get; set; }
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
        public List<TimeZone> TimeZones { get; private set; } = new List<TimeZone>();
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
        public ObservableCollection<Log> MinorLogList { set; private get; } = new ObservableCollection<Log>();
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
            OrderStatus.PendingSubmit, OrderStatus.Submitted, OrderStatus.PartiallyFilled };
        public static IEnumerable<OrderInfo> GetUnfilledOrderInfo(IEnumerable<OrderInfo> orderInfos)
        {
            OrderInfo info = orderInfos.Where(x => x.OrderStatus == null || IncompleteStatus.Any(y => y == x.OrderStatus?.Status)).LastOrDefault();
            if (info != null)
                return orderInfos.Where(x => x.BatchNo == info.BatchNo && (x.OrderStatus == null || 
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

            TimeZones.Add(new TimeZone { Id = "HKT", UtcOffset = new TimeSpan(8,0,0), Description = "Asia/Hong_Kong" });
            TimeZones.Add(new TimeZone { Id = "EST", UtcOffset = new TimeSpan(-5, 0, 0), Description = "Eastern Standard Time (North America)" });

            Weekdays.Add(new WeekDay { Value = 0, Name = "Sun" });
            Weekdays.Add(new WeekDay { Value = 1, Name = "Mon" });
            Weekdays.Add(new WeekDay { Value = 2, Name = "Tue" });
            Weekdays.Add(new WeekDay { Value = 3, Name = "Wed" });
            Weekdays.Add(new WeekDay { Value = 4, Name = "Thu" });
            Weekdays.Add(new WeekDay { Value = 5, Name = "Fri" });
            Weekdays.Add(new WeekDay { Value = 6, Name = "Sat" });

            StopLossSource.Add("User defined");
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
                    SymbolInActions.Add(symbol);
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
        public void Log(Log log)
        {
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                if (LogList.Count > 0 && log.Text == LogList[0].Text && log.Source == LogList[0].Source)
                    LogList[0].Time = log.Time;
                else
                {
                    LogList.Insert(0, log);
                    
                    if (File.Exists(logfile))
                    {
                        using (var sw = new StreamWriter(logfile, true))
                        {
                            sw.WriteLine(ListViewHelper.ObjectToLine(log));
                        }
                    }
                }
                    
            });
        }
        public void MinorLog(Log log)
        {
            bool found = false;
            if (UserPreference != null && UserPreference.ErrorFilter != null)
            {
                string[] filters = UserPreference.ErrorFilter.Split(new char[] { ';' });
                for (int i = 0; i < filters.Length; i++)
                {
                    if (filters[i] != string.Empty && filters[i] != null && log.Text.ToLower().Contains(filters[i].ToLower()))
                    {
                        found = true;
                        break;
                    }
                }
            }
            
            if (!OrderManager.MainWin.MinorLogPause && !found)
                Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
                {
                    MinorLogList.Insert(0, log);
                    
                    if (File.Exists(minorlogfile))
                    {
                        using (var sw = new StreamWriter(minorlogfile, true))
                        {
                            sw.WriteLine(ListViewHelper.ObjectToLine(log));
                        }
                    }
                });
        }
        public void AddMessage(Message msg)
        {
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                MessageList.Insert(0, msg);
                if (File.Exists(msgfile))
                {
                    using (var sw = new StreamWriter(msgfile, true))
                    {
                        sw.WriteLine(ListViewHelper.ObjectToLine(msg));
                    }
                }
            });
        }

        private void InsertOrder(DisplayedOrder order)
        {
            Dispatcher.FromThread(OrderManager.UIThread).Invoke(() =>
            {
                Orders.Insert(0, order);
                CreateLoggingFiles();
                if (File.Exists(orderfile))
                {
                    using (var sw = new StreamWriter(orderfile, true))
                    {
                        sw.WriteLine(ListViewHelper.ObjectToLine(order));
                    }
                }
            });
        }
        
        private void Orders_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            PendingOrdersView.Refresh();
            ExecutionView.Refresh();

            CreateLoggingFiles();
            if (File.Exists(orderfile))
            {
                using (var sw = new StreamWriter(orderfile, true))
                {
                    foreach (var item in e.NewItems)
                    {
                        sw.WriteLine(ListViewHelper.ObjectToLine(item));
                    }                    
                }
            }
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

        public void ReadSettings()
        {
            UserPreference = JsonConvert.DeserializeObject<UserPreference>(Properties.Settings.Default["preference"].ToString());
            List<IController> ctrls = new List<IController>();
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
                                ctrls.Add(ic);
                            }
                            else
                                ctrls.Add(ctrl);
                        }
                    }
                    Controllers.Clear();
                    foreach (var item in ctrls)
                    {
                        Controllers.Add(item);
                    }
                }
            }

        }
    }
}
