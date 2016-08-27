using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Devices.Geolocation;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls.Maps;
using NMEA;

namespace HoloGps
{
    public sealed partial class MainPage
    {
        /// <summary>
        /// Search for the GPS receiver's name in the list of bluetooth devices
        /// </summary>
        /// <remarks>Change this to the name of your device or portion of its name</remarks>
        private const string RecieverSearch = "BT-GPS"; // I use a portion of my device's name, which was BT-GPS-35

        private StreamSocket _socket;

        private RfcommDeviceService _service;
        private DataReader _dataReaderObject;
        private CancellationTokenSource _readCancellationTokenSource;
        private StringBuilder _buffer;
        delegate T NullChecker<T>(object parameter);
        readonly NullChecker<double> _doubleNullChecker = (x => (double?)x ?? double.NaN);
        private DateTime _nextCheck;

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Socket cleanup
            DropSocket();
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Used for a periodic update to the map
            _nextCheck = DateTime.Now;
            if (WorldMap.Is3DSupported)
            {
                WorldMap.Style = MapStyle.Aerial3DWithRoads;
            }
            // Capture the device
            ListRfSerialDevices();
        }

        private async void ListRfSerialDevices()
        {
            var devices =
            await DeviceInformation.FindAllAsync(RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort));

            if (devices != null)
            {
                Debug.WriteLine("{0} devices found", devices.Count);
                foreach (var device1 in devices)
                {
                    Debug.WriteLine("{0} {1} {2}", device1.Id, device1.Name, device1.Kind);
                    if (device1.Name.Contains(RecieverSearch))
                    {
                        var device = device1;
                        try
                        {
                            _service = await RfcommDeviceService.FromIdAsync(
                                device.Id);

                            _socket = new StreamSocket();
                            // Using a cancellation token to cancel connection attempt after 10 seconds.
                            // Can try 5 seconds. But anything below that will probably return a false-positive
                            var cts = new CancellationTokenSource();
                            cts.CancelAfter(10000);

                            var op = _socket.ConnectAsync(
                                _service.ConnectionHostName,
                                _service.ConnectionServiceName,
                                SocketProtectionLevel.
                                    BluetoothEncryptionAllowNullAuthentication);
                            var aTask = op.AsTask(cts.Token);
                            await aTask;
                            Debug.WriteLine("Connected: {0} {1}", _service.ConnectionHostName,
                                _service.ConnectionServiceName);
                            _buffer = new StringBuilder();
                            await Task.Run(() => { Listen(); });
                            break;
                        }
                        catch (Exception)
                        {
                            Debug.WriteLine("Failed");
                        }
                    }
                }

            }
        }

        private async void Listen()
        {
            _readCancellationTokenSource = new CancellationTokenSource();

            if (_socket.InputStream == null) { return; }
            _dataReaderObject = new DataReader(_socket.InputStream);
            while (true)
            {
                await ReadAsync(_readCancellationTokenSource.Token);
            }
        }

        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            uint readBufferLength = 16384;
            // If task cancellation was requested, comply

            // Set InputStreamOptions to complete the asynchronous read operation when one or more bytes is available
            _dataReaderObject.InputStreamOptions = InputStreamOptions.Partial;

            // Create a task object to wait for data on the serialPort.InputStream
            var loadAsyncTask = _dataReaderObject.LoadAsync(readBufferLength).AsTask(cancellationToken);

            // Launch the task and wait
            var bytesRead = await loadAsyncTask;
            if (bytesRead > 0)
            {
                try
                {
                    var bytes = new byte[bytesRead];
                    _dataReaderObject.ReadBytes(bytes);
                    var recvdtxt = Encoding.UTF8.GetString(bytes);
                    OnIncomingData(recvdtxt);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ReadAsync: " + ex.Message);
                }
            }
        }

        private void UpdatePosition(double latitude, double longitude)
        {
            try
            {
                var position = new BasicGeoposition();
                position.Latitude = latitude;
                position.Longitude = longitude;

                var location = new Geopoint(position);

                var mapScene = MapScene.CreateFromLocationAndRadius(location, 500);
                var trySetSceneAsync = WorldMap.TrySetSceneAsync(mapScene);
                if (!trySetSceneAsync.GetResults())
                {
                    Debug.WriteLine("Set scene failed but that's okay.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private async void OnIncomingData(string data)
        {
            _buffer.Append(data);
            var temp = _buffer.ToString();

            var lIndex = temp.LastIndexOf(NMEAParser.SentenceEndDelimiter, StringComparison.Ordinal);
            if (lIndex >= 0)
            {
                _buffer = _buffer.Remove(0, lIndex + 2);
                if (lIndex + 2 < temp.Length)
                    temp = temp.Remove(lIndex + 2);

                temp = temp.Trim('\0');

                var lines = temp.Split(NMEAParser.SentenceEndDelimiter.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                if (DateTime.Now > _nextCheck)
                {
                    foreach (var t in lines)
                    {
                        try
                        {
                            var result = NMEAParser.Parse(t + NMEAParser.SentenceEndDelimiter);

                            var sResult = result as NMEAStandartSentence;
                            if (sResult?.SentenceID == SentenceIdentifiers.GGA)
                            {
                                var parameters = sResult.parameters;
                                var gpsQualityIndicator = (string)parameters[5];
                                if (gpsQualityIndicator != "Fix not availible")
                                {
                                    try
                                    {
                                        var lat = _doubleNullChecker(parameters[1]);
                                        var northSouth = (string)(parameters[2] ?? string.Empty);
                                        Debug.WriteLine("{0} {1} {2} {3}", parameters[1], parameters[2], parameters[3], parameters[4]);
                                        lat = northSouth == "N" ? lat : -lat;
                                        var lon = _doubleNullChecker(parameters[3]);
                                        var eastWest = (string)(parameters[4] ?? string.Empty);
                                        lon = eastWest == "E" ? lon : -lon;
                                        Debug.WriteLine("Lat: {0} Long: {1}", lat, lon);
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                                        {
                                            UpdatePosition(lat, lon);
                                        });

                                    }
                                    catch (Exception)
                                    {
                                        // ignored
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex);
                        }
                    }
                    _nextCheck = DateTime.Now.AddSeconds(2);
                }
            }

            if (_buffer.Length >= ushort.MaxValue)
                _buffer.Remove(0, short.MaxValue);
        }

        private async void DropSocket()
        {
            try
            {
                if (_readCancellationTokenSource != null)
                {
                    if (!_readCancellationTokenSource.IsCancellationRequested)
                    {
                        _readCancellationTokenSource.Cancel();
                    }
                }
                await _socket.CancelIOAsync();
                _socket.Dispose();
                _socket = null;
                _service.Dispose();
                _service = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }


    }
}
