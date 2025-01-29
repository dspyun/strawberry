using HeartRateLE.Bluetooth.Events;
using HeartRateLE.Bluetooth.Schema;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace HeartRateLE.Bluetooth
{
    public class HeartRateMonitor
    {
        private BluetoothLEDevice _heartRateDevice = null;
        private List<BluetoothAttribute> _serviceCollection = new List<BluetoothAttribute>();

        private BluetoothAttribute _TemperatureAttribute;
        private BluetoothAttribute _HumidityAttribute;
        private BluetoothAttribute _envSensingAttribute;
        private GattCharacteristic _TemperatureCharacteristic;
        private GattCharacteristic _HumidityCharacteristic;

        /// <summary>
        /// Occurs when [connection status changed].
        /// </summary>
        public event EventHandler<Events.ConnectionStatusChangedEventArgs> ConnectionStatusChanged;
        /// <summary>
        /// Raises the <see cref="E:ConnectionStatusChanged" /> event.
        /// </summary>
        /// <param name="e">The <see cref="Events.ConnectionStatusChangedEventArgs"/> instance containing the event data.</param>
        protected virtual void OnConnectionStatusChanged(Events.ConnectionStatusChangedEventArgs e)
        {
            ConnectionStatusChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Occurs when [value changed].
        /// </summary>
        public event EventHandler<Events.TempChangedEventArgs> TempChanged;
        public event EventHandler<Events.HumiChangedEventArgs> HumiChanged;


        /// <summary>
        /// Raises the <see cref="E:ValueChanged" /> event.
        /// </summary>
        /// <param name="e">The <see cref="Events.TempChangedEventArgs"/> instance containing the event data.</param>
        protected virtual void OnTempChanged(Events.TempChangedEventArgs e)
        {
            TempChanged?.Invoke(this, e);
        }
        protected virtual void OnHumiChanged(Events.HumiChangedEventArgs e)
        {
            HumiChanged?.Invoke(this, e);
        }

        public void get_deivce(string targetDeviceName)
        {
            BluetoothLEAdvertisementWatcher watcher = new BluetoothLEAdvertisementWatcher();
            watcher.Received += async (sender, eventArgs) =>
            {
                // Get advertisement properties
                var advertisement = eventArgs.Advertisement;
                var localName = advertisement.LocalName;

                // Check if the advertisement name matches the target device name
                if (!string.IsNullOrEmpty(localName) && localName == targetDeviceName)
                {
                    Console.WriteLine($"Found device: {localName}");
                    Console.WriteLine($"Bluetooth Address: {eventArgs.BluetoothAddress:X}");

                    // Stop the watcher once the target device is found
                    watcher.Stop();

                    // Connect to the device
                    var device = await BluetoothLEDevice.FromBluetoothAddressAsync(eventArgs.BluetoothAddress);
                    if (device != null)
                    {
                        _heartRateDevice = device;
                    }
                    else
                    {
                        Console.WriteLine("Failed to connect to the device.");
                    }
                }
            };
        }

        public async Task<ConnectionResult> ConnectAsync(string deviceId)
        {
            _heartRateDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
            get_deivce("Nordic_HRR");
            if (_heartRateDevice == null)
            {
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = "Could not find specified heart rate device"
                };
            }

            if (!_heartRateDevice.DeviceInformation.Pairing.IsPaired)
            {
                _heartRateDevice = null;
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = "Heart rate device is not paired"
                };
            }

            // we should always monitor the connection status
            _heartRateDevice.ConnectionStatusChanged -= DeviceConnectionStatusChanged;
            _heartRateDevice.ConnectionStatusChanged += DeviceConnectionStatusChanged;

            var isReachable = await GetDeviceServicesAsync();
            if (!isReachable)
            {
                _heartRateDevice = null;
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = "Heart rate device is unreachable (i.e. out of range or shutoff)"
                };
            }

            _envSensingAttribute = _serviceCollection.Where(a => a.Name == "EnvSensing").FirstOrDefault();
            if (_envSensingAttribute == null)
            {
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = "Cannot find Env Sensing service"
                };
            }

            CharacteristicResult temp_characteristicResult;
            temp_characteristicResult = await SetupTemperatureCharacteristic(_envSensingAttribute);
            if (!temp_characteristicResult.IsSuccess)
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = temp_characteristicResult.Message
                };
            
            CharacteristicResult humi_characteristicResult;
            humi_characteristicResult = await SetupHumidityCharacteristic(_envSensingAttribute);
            if (!humi_characteristicResult.IsSuccess)
                return new Schema.ConnectionResult()
                {
                    IsConnected = false,
                    ErrorMessage = humi_characteristicResult.Message
                };
            
            // we could force propagation of event with connection status change, to run the callback for initial status
            DeviceConnectionStatusChanged(_heartRateDevice, null);

            return new Schema.ConnectionResult()
            {
                IsConnected = _heartRateDevice.ConnectionStatus == BluetoothConnectionStatus.Connected,
                Name = _heartRateDevice.Name
            };
        }

        private async Task<List<BluetoothAttribute>> GetServiceCharacteristicsAsync(BluetoothAttribute service)
        {
            IReadOnlyList<GattCharacteristic> characteristics = null;
            try
            {
                // Ensure we have access to the device.
                var accessStatus = await service.service.RequestAccessAsync();
                if (accessStatus == DeviceAccessStatus.Allowed)
                {
                    // BT_Code: Get all the child characteristics of a service. Use the cache mode to specify uncached characterstics only 
                    // and the new Async functions to get the characteristics of unpaired devices as well. 
                    var result = await service.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        characteristics = result.Characteristics;
                    }
                    else
                    {
                        characteristics = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    // Not granted access
                    // On error, act as if there are no characteristics.
                    characteristics = new List<GattCharacteristic>();
                }
            }
            catch (Exception ex)
            {
                characteristics = new List<GattCharacteristic>();
            }

            var characteristicCollection = new List<BluetoothAttribute>();
            characteristicCollection.AddRange(characteristics.Select(a => new BluetoothAttribute(a)));
            return characteristicCollection;
        }

        private async Task<CharacteristicResult> SetupTemperatureCharacteristic(BluetoothAttribute _serviceAttribute)
        {
            // TemperatureMeasurement is BT_UUID_HTS_MEASUREMENT_VAL in device
            var characteristics = await GetServiceCharacteristicsAsync(_serviceAttribute);
            _TemperatureAttribute = characteristics.Where(a => a.Name == "Temperature").FirstOrDefault();
            if (_TemperatureAttribute == null)
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = "Cannot find HeartRateMeasurement characteristic"
                };
            }
            _TemperatureCharacteristic = _TemperatureAttribute.characteristic;

            // Get all the child descriptors of a characteristics. Use the cache mode to specify uncached descriptors only 
            // and the new Async functions to get the descriptors of unpaired devices as well. 
            var result = await _TemperatureCharacteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = result.Status.ToString()
                };
            }

            if (_TemperatureCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                var status = await _TemperatureCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (status == GattCommunicationStatus.Success)
                {
                    _TemperatureCharacteristic.ValueChanged += TemperatureValueChanged;

                }

                return new CharacteristicResult()
                {
                    IsSuccess = status == GattCommunicationStatus.Success,
                    Message = status.ToString()
                };
            }
            else
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = "HeartRateMeasurement characteristic does not support notify"
                };

            }
        }
        private async Task<CharacteristicResult> SetupHumidityCharacteristic(BluetoothAttribute _serviceAttribute)
        {
            var characteristics1 = await GetServiceCharacteristicsAsync(_serviceAttribute);
            // TemperatureMeasurement is BT_UUID_HTS_MEASUREMENT_VAL in device
            _HumidityAttribute = characteristics1.Where(a => a.Name == "Humidity").FirstOrDefault();
            if (_HumidityAttribute == null)
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = "Cannot find HeartRateMeasurement characteristic"
                };
            }
            _HumidityCharacteristic = _HumidityAttribute.characteristic;

            // Get all the child descriptors of a characteristics. Use the cache mode to specify uncached descriptors only 
            // and the new Async functions to get the descriptors of unpaired devices as well. 
            var result = await _HumidityCharacteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = result.Status.ToString()
                };
            }

            if (_HumidityCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                var status = await _HumidityCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (status == GattCommunicationStatus.Success)
                {
                    _HumidityCharacteristic.ValueChanged += HumidityValueChanged;

                }

                return new CharacteristicResult()
                {
                    IsSuccess = status == GattCommunicationStatus.Success,
                    Message = status.ToString()
                };
            }
            else
            {
                return new CharacteristicResult()
                {
                    IsSuccess = false,
                    Message = "HeartRateMeasurement characteristic does not support notify"
                };

            }
        }
        private async Task<bool> GetDeviceServicesAsync()
        {
            // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
            // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
            // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
            GattDeviceServicesResult result = await _heartRateDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

            if (result.Status == GattCommunicationStatus.Success)
            {
                _serviceCollection.AddRange(result.Services.Select(a => new BluetoothAttribute(a)));
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Disconnects the current BLE heart rate device.
        /// </summary>
        /// <returns></returns>
        public async Task DisconnectAsync()
        {
            
            if (_heartRateDevice != null)
            {
                if (_TemperatureCharacteristic != null)
                {
                    //NOTE: might want to do something here if the result is not successful
                    var result = await _TemperatureCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                    if (_TemperatureCharacteristic.Service != null)
                        _TemperatureCharacteristic.Service.Dispose();
                    _TemperatureCharacteristic = null;
                }

                if (_TemperatureAttribute != null)
                {
                    if (_TemperatureAttribute.service != null)
                        _TemperatureAttribute.service.Dispose();
                    _TemperatureAttribute = null;
                }

                if (_envSensingAttribute != null)
                {
                    if (_envSensingAttribute.service != null)
                        _envSensingAttribute.service.Dispose();
                    _envSensingAttribute = null;
                }

                _serviceCollection = new List<BluetoothAttribute>();

                _heartRateDevice.Dispose();
                _heartRateDevice = null;

                DeviceConnectionStatusChanged(null, null);
            }
            
        }
        private void DeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            var result = new ConnectionStatusChangedEventArgs()
            {
                IsConnected = sender != null && (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            };

            OnConnectionStatusChanged(result);
        }

        private void TemperatureValueChanged(GattCharacteristic sender, GattValueChangedEventArgs e)
        {
            byte[] data;
            CryptographicBuffer.CopyToByteArray(e.CharacteristicValue, out data);

            var reader = DataReader.FromBuffer(e.CharacteristicValue);
            byte[] rawData = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(rawData);

            var args = new Events.TempChangedEventArgs()
            {
                TempLevel = Utilities.ParseTemperature(rawData)
            };
            OnTempChanged(args);
            /*
            var reader = DataReader.FromBuffer(e.CharacteristicValue);
            byte[] rawData = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(rawData);
            temp_parse(rawData);
            //temp_meas_parse(rawData);
            */
        }

        private void HumidityValueChanged(GattCharacteristic sender, GattValueChangedEventArgs e)
        {
            byte[] data;
            //CryptographicBuffer.CopyToByteArray(e.CharacteristicValue, out data);

            var reader = DataReader.FromBuffer(e.CharacteristicValue);
            byte[] rawData = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(rawData);

            var args = new Events.HumiChangedEventArgs()
            {
                HumiLevel = Utilities.ParseHumidity(rawData)
            };
            OnHumiChanged(args);
        }

        private static void temp_meas_parse(byte[] rawData)
        {
            // Print the raw data in hexadecimal format
            Console.WriteLine("Raw Data (Hex): " + BitConverter.ToString(rawData).Replace("-", " "));


            if (rawData.Length < 5)
            {
                Console.WriteLine("Invalid data length.");
                return;
            }

            // Extract flags
            byte flags = rawData[0];
            bool isFahrenheit = (flags & 0x01) != 0;
            bool hasTimestamp = (flags & 0x02) != 0;
            bool hasTemperatureType = (flags & 0x04) != 0;

            // Parse temperature (IEEE 11073 32-bit float: mantissa + exponent)
            int mantissa = BitConverter.ToInt32(rawData, 1) & 0x00FFFFFF;
            int exponent = (sbyte)((rawData[4] & 0xFF000000) >> 24);
            double temperature = mantissa * Math.Pow(10, exponent) * 0.01;

            Console.WriteLine($"Temperature: {temperature:0.00} {(isFahrenheit ? "°F" : "°C")}");

            // Parse timestamp if present
            if (hasTimestamp && rawData.Length >= 12)
            {
                int year = BitConverter.ToUInt16(rawData, 5);
                int month = rawData[7];
                int day = rawData[8];
                int hour = rawData[9];
                int minute = rawData[10];
                int second = rawData[11];

                Console.WriteLine($"Timestamp: {year:0000}-{month:00}-{day:00} {hour:00}:{minute:00}:{second:00}");
            }

            // Parse temperature type if present
            if (hasTemperatureType && rawData.Length >= 13)
            {
                byte temperatureType = rawData[12];
                Console.WriteLine($"Temperature Type: {temperatureType}");
            }
        }


        /// <summary>
        /// Gets a value indicating whether this instance is connected.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance is connected; otherwise, <c>false</c>.
        /// </value>
        public bool IsConnected
        {
            get { return _heartRateDevice != null ? _heartRateDevice.ConnectionStatus == BluetoothConnectionStatus.Connected : false; }
        }

        /// <summary>
        /// Gets the device information for the current BLE heart rate device.
        /// </summary>
        /// <returns></returns>
        public async Task<Schema.HeartRateDeviceInfo> GetDeviceInfoAsync()
        {
            if (_heartRateDevice != null && _heartRateDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                var deviceInfoService = _serviceCollection.Where(a => a.Name == "DeviceInformation").FirstOrDefault();
                var deviceInfocharacteristics = await GetServiceCharacteristicsAsync(deviceInfoService);

                var batteryService = _serviceCollection.Where(a => a.Name == "Battery").FirstOrDefault();
                var batteryCharacteristics = await GetServiceCharacteristicsAsync(batteryService);
                //byte battery = await _batteryParser.ReadAsync();

                return new Schema.HeartRateDeviceInfo()
                {
                    DeviceId = _heartRateDevice.DeviceId,
                    Name = _heartRateDevice.Name,
                    Firmware = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "FirmwareRevisionString"),
                    Hardware = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "HardwareRevisionString"),
                    Manufacturer = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "ManufacturerNameString"),
                    SerialNumber = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "SerialNumberString"),
                    ModelNumber = await Utilities.ReadCharacteristicValueAsync(deviceInfocharacteristics, "ModelNumberString"),
                    BatteryPercent = Convert.ToInt32(await Utilities.ReadCharacteristicValueAsync(batteryCharacteristics, "BatteryLevel"))
                };
            }
            else
            {
                return new Schema.HeartRateDeviceInfo();
            }
        }
    }
}