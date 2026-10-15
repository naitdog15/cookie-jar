using System;
using System.ComponentModel;
using System.Globalization;

namespace CookieJar.Core
{
    public class RobloxInstance : INotifyPropertyChanged
    {
        private int _id;
        private string _name;
        private int _pid;
        private DateTime _startedUtc;
        private string _status;
        private double _memoryMb;
        private string _uptime;
        private int _childCount;
        private DateTime? _exitedUtc;
        private string _lastError;

        public event PropertyChangedEventHandler PropertyChanged;

        public int Id
        {
            get { return _id; }
            set
            {
                if (_id != value)
                {
                    _id = value;
                    OnPropertyChanged("Id");
                }
            }
        }

        public string Name
        {
            get { return _name; }
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged("Name");
                }
            }
        }

        private string _profileName;
        private bool _isAdopted;
        private string _accountName;

        /// <summary>Username of the stored account this client was launched for, or null.</summary>
        public string AccountName
        {
            get { return _accountName; }
            set
            {
                if (_accountName != value)
                {
                    _accountName = value;
                    OnPropertyChanged("AccountName");
                }
            }
        }

        /// <summary>True when this client was already running and was adopted, not launched here.</summary>
        public bool IsAdopted
        {
            get { return _isAdopted; }
            set
            {
                if (_isAdopted != value)
                {
                    _isAdopted = value;
                    OnPropertyChanged("IsAdopted");
                }
            }
        }

        /// <summary>Saved profile this instance was launched with, or null for a fresh instance.</summary>
        public string ProfileName
        {
            get { return _profileName; }
            set
            {
                if (_profileName != value)
                {
                    _profileName = value;
                    OnPropertyChanged("ProfileName");
                }
            }
        }

        public int Pid
        {
            get { return _pid; }
            set
            {
                if (_pid != value)
                {
                    _pid = value;
                    OnPropertyChanged("Pid");
                }
            }
        }

        public DateTime StartedUtc
        {
            get { return _startedUtc; }
            set
            {
                if (_startedUtc != value)
                {
                    _startedUtc = value;
                    OnPropertyChanged("StartedUtc");
                }
            }
        }

        public string Status
        {
            get { return _status; }
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged("Status");
                    OnPropertyChanged("StatusKind");
                    OnPropertyChanged("IsActive");
                }
            }
        }

        public double MemoryMb
        {
            get { return _memoryMb; }
            set
            {
                if (_memoryMb != value)
                {
                    _memoryMb = value;
                    OnPropertyChanged("MemoryMb");
                    OnPropertyChanged("MemoryText");
                }
            }
        }

        public string Uptime
        {
            get { return _uptime; }
            set
            {
                if (_uptime != value)
                {
                    _uptime = value;
                    OnPropertyChanged("Uptime");
                }
            }
        }

        /// <summary>Number of descendant processes of this instance.</summary>
        public int ChildCount
        {
            get { return _childCount; }
            set
            {
                if (_childCount != value)
                {
                    _childCount = value;
                    OnPropertyChanged("ChildCount");
                }
            }
        }

        /// <summary>
        /// Coarse status category derived from Status:
        /// "Starting", "Running", "Stopped" or "Error".
        /// </summary>
        public string StatusKind
        {
            get
            {
                string s = _status;
                if (s == null)
                {
                    return "Stopped";
                }
                if (s.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                {
                    return "Error";
                }
                if (string.Equals(s, "Starting", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "Awaiting login", StringComparison.OrdinalIgnoreCase))
                {
                    return "Starting";
                }
                if (string.Equals(s, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    return "Running";
                }
                return "Stopped";
            }
        }

        /// <summary>True when Status is set and is not "Stopped".</summary>
        public bool IsActive
        {
            get { return _status != null && _status != "Stopped"; }
        }

        /// <summary>Memory formatted as "512 MB" or "1.2 GB" (invariant culture).</summary>
        public string MemoryText
        {
            get
            {
                if (_memoryMb >= 1024.0)
                {
                    return string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", _memoryMb / 1024.0);
                }
                return string.Format(CultureInfo.InvariantCulture, "{0:0} MB", _memoryMb);
            }
        }

        /// <summary>UTC time the process exited, or null if still running / never started.</summary>
        public DateTime? ExitedUtc
        {
            get { return _exitedUtc; }
            set
            {
                if (_exitedUtc != value)
                {
                    _exitedUtc = value;
                    OnPropertyChanged("ExitedUtc");
                }
            }
        }

        /// <summary>Last error message reported for this instance, if any.</summary>
        public string LastError
        {
            get { return _lastError; }
            set
            {
                if (_lastError != value)
                {
                    _lastError = value;
                    OnPropertyChanged("LastError");
                }
            }
        }

        public override string ToString()
        {
            return Name + " (PID " + Pid.ToString(CultureInfo.InvariantCulture) + ")";
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
