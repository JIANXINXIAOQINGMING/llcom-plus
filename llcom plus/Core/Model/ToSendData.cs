using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace llcom_plus.Model
{
    public class ToSendData : INotifyPropertyChanged
    {
        public static event EventHandler DataChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        private int _id;
        private string _text;
        private bool _hex;
        private string _commit;
        private string _recvScriptPath = "";
        private string _recvScriptPara = "";
        private bool _appendCrlf;
        private bool _disableSuggestion;
        public int id
        {
            get
            {
                return _id;
            }
            set
            {
                _id = value;
                //DataChanged?.Invoke(0, EventArgs.Empty);
                OnPropertyChanged(nameof(id));
            }
        }
        public string text
        {
            get
            {
                return _text;
            }
            set
            {
                _text = value;
                DataChanged?.Invoke(0, EventArgs.Empty);
                OnPropertyChanged(nameof(text));
            }
        }
        public bool hex
        {
            get
            {
                return _hex;
            }
            set
            {
                _hex = value;
                DataChanged?.Invoke(0, EventArgs.Empty);
                OnPropertyChanged(nameof(hex));
            }
        }

        public string commit
        {
            get
            {
                return _commit;
            }
            set
            {
                _commit = value;
                DataChanged?.Invoke(0, EventArgs.Empty);
                OnPropertyChanged(nameof(commit));
            }
        }

        public string recvScriptPath
        {
            get
            {
                return _recvScriptPath;
            }
            set
            {
                _recvScriptPath = value;
                DataChanged?.Invoke(0, EventArgs.Empty);
                OnPropertyChanged(nameof(recvScriptPath));
            }
        }

        public string recvScriptPara {
            get { return _recvScriptPara; }
            set { _recvScriptPara = value; DataChanged?.Invoke(0, EventArgs.Empty); OnPropertyChanged(nameof(recvScriptPara)); } }

        public bool disableSuggestion
        {
            get
            {
                return _disableSuggestion;
            }
            set
            {
                _disableSuggestion = value;
                DataChanged?.Invoke(0, EventArgs.Empty);
                OnPropertyChanged(nameof(disableSuggestion));
            }
        }

        public bool appendCrlf
        {
            get
            {
                return _appendCrlf;
            }
            set
            {
                _appendCrlf = value;
                DataChanged?.Invoke(0, EventArgs.Empty);
                OnPropertyChanged(nameof(appendCrlf));
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
