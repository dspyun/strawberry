using HeartRateLE.Bluetooth.Schema;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using System.Linq;

namespace HeartRateLE.Bluetooth
{
    public static class Utilities
    {
        static float temperature;
        static float humidity;

        public static async Task<string> ReadCharacteristicValueAsync(List<BluetoothAttribute> characteristics, string characteristicName)
        {
            var characteristic = characteristics.FirstOrDefault(a => a.Name == characteristicName)?.characteristic;
            if (characteristic == null)
                return "0";

            var readResult = await characteristic.ReadValueAsync();

            if (readResult.Status == GattCommunicationStatus.Success)
            {
                byte[] data;
                CryptographicBuffer.CopyToByteArray(readResult.Value, out data);

                if (characteristic.Uuid.Equals(GattCharacteristicUuids.BatteryLevel))
                {
                    try
                    {
                        // battery level is encoded as a percentage value in the first byte according to
                        // https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.battery_level.xml
                        return data[0].ToString();
                    }
                    catch (ArgumentException)
                    {
                        return "0";
                    }
                }
                else
                    return Encoding.UTF8.GetString(data);
            }
            else
            {
                return $"Read failed: {readResult.Status}";
            }
        }


        /// <summary>
        ///     Converts from standard 128bit UUID to the assigned 32bit UUIDs. Makes it easy to compare services
        ///     that devices expose to the standard list.
        /// </summary>
        /// <param name="uuid">UUID to convert to 32 bit</param>
        /// <returns></returns>
        public static ushort ConvertUuidToShortId(Guid uuid)
        {
            // Get the short Uuid
            var bytes = uuid.ToByteArray();
            var shortUuid = (ushort)(bytes[0] | (bytes[1] << 8));
            return shortUuid;
        }

        /// <summary>
        ///     Converts from a buffer to a properly sized byte array
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public static byte[] ReadBufferToBytes(IBuffer buffer)
        {
            var dataLength = buffer.Length;
            var data = new byte[dataLength];
            using (var reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(data);
            }
            return data;
        }

        /// <summary>
        /// Process the raw data received from the device into application usable data,
        /// according the the Bluetooth Heart Rate Profile.
        /// https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.heart_rate_measurement.xml&u=org.bluetooth.characteristic.heart_rate_measurement.xml
        /// This function throws an exception if the data cannot be parsed.
        /// </summary>
        /// <param name="data">Raw data received from the heart rate monitor.</param>
        /// <returns>The heart rate measurement value.</returns>
        public static ushort ParseHeartRateValue(byte[] data)
        {
            // Heart Rate profile defined flag values
            const byte heartRateValueFormat = 0x01;

            byte flags = data[0];
            bool isHeartRateValueSizeLong = ((flags & heartRateValueFormat) != 0);

            if (isHeartRateValueSizeLong)
            {
                return BitConverter.ToUInt16(data, 1);
            }
            else
            {
                return data[1];
            }
        }
        public static float ParseHumidity(byte[] data)
        {
            // Heart Rate profile defined flag values
            //Console.WriteLine("Humidity Raw Data (Hex): " + BitConverter.ToString(data).Replace("-", " "));

            // Parse humidity (16-bit value in 0.01% RH)
            if (data.Length >= 2)
            {
                double humidityRaw = BitConverter.ToInt16(data, 0);
                humidity = (float)(humidityRaw * 0.01);


                //Console.WriteLine($"Humidity: {humidity:0.00} %RH");
                return humidity;
            }
            return humidity;
        }
        public static float ParseTemperature(byte[] data)
        {
            //Console.WriteLine("Temperature Raw Data (Hex): " + BitConverter.ToString(data).Replace("-", " "));

            // Parse temperature (IEEE-11073 32-bit float)
            if (data.Length >= 4)
            {
                // Parse temperature
                short temperatureHundredths = BitConverter.ToInt16(data, 0);

                // Convert to floating-point degrees Celsius
                temperature = (float)(temperatureHundredths / 100.0);

                //Console.WriteLine($"Temperature: {temperature:0.00} °C");
                return temperature;
            }
            return temperature;
        }

        public static float ParseTemperatureMeasurement(byte[] data)
        {
            // Print the raw data in hexadecimal format
            Console.WriteLine("Raw Data (Hex): " + BitConverter.ToString(data).Replace("-", " "));


            if (data.Length < 5)
            {
                Console.WriteLine("Invalid data length.");
                return 0;
            }

            // Extract flags
            byte flags = data[0];
            bool isFahrenheit = (flags & 0x01) != 0;
            bool hasTimestamp = (flags & 0x02) != 0;
            bool hasTemperatureType = (flags & 0x04) != 0;

            // Parse temperature (IEEE 11073 32-bit float: mantissa + exponent)
            int mantissa = BitConverter.ToInt32(data, 1) & 0x00FFFFFF;
            int exponent = (sbyte)((data[4] & 0xFF000000) >> 24);
            double temperature = mantissa * Math.Pow(10, exponent) * 0.01;

            Console.WriteLine($"Temperature: {temperature:0.00} {(isFahrenheit ? "°F" : "°C")}");

            // Parse timestamp if present
            if (hasTimestamp && data.Length >= 12)
            {
                int year = BitConverter.ToUInt16(data, 5);
                int month = data[7];
                int day = data[8];
                int hour = data[9];
                int minute = data[10];
                int second = data[11];

                Console.WriteLine($"Timestamp: {year:0000}-{month:00}-{day:00} {hour:00}:{minute:00}:{second:00}");
            }

            // Parse temperature type if present
            if (hasTemperatureType && data.Length >= 13)
            {
                byte temperatureType = data[12];
                Console.WriteLine($"Temperature Type: {temperatureType}");
            }
            return (float)temperature;
        }
    }
}
