using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HeartRateLE.Bluetooth.Events
{
    public class TempChangedEventArgs : EventArgs
    {
        public float TempLevel { get; set; }
    }
    public class HumiChangedEventArgs : EventArgs
    {
        public float HumiLevel { get; set; }
    }

}
