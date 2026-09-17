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
        private int _responseMode;
        private string _expectedResponse = "";
        private int _responseTimeoutMs = 5000;
        private int _responseRetries;
        private bool _skipInWorkflow;
        // View-only state; never add derived UI fields to saved/imported data.
        [Newtonsoft.Json.JsonIgnore]
        public bool HasCustomOptions => hex || !appendCrlf || disableSuggestion ||
            !string.IsNullOrWhiteSpace(recvScriptPath) || !string.IsNullOrWhiteSpace(recvScriptPara) ||
            responseMode != 0 || !string.IsNullOrEmpty(expectedResponse) || skipInWorkflow ||
            responseTimeoutMs != 5000 || responseRetries != 0;

        // Additive fields keep legacy quick-send files as single-send commands.
        public int responseMode
        {
            get => _responseMode;
            set { _responseMode = value; Changed(nameof(responseMode)); }
        }
        public string expectedResponse
        {
            get => _expectedResponse;
            set { _expectedResponse = value ?? ""; Changed(nameof(expectedResponse)); }
        }
        public int responseTimeoutMs
        {
            get => _responseTimeoutMs;
            set { _responseTimeoutMs = value; Changed(nameof(responseTimeoutMs)); }
        }
        public int responseRetries
        {
            get => _responseRetries;
            set { _responseRetries = value; Changed(nameof(responseRetries)); }
        }
        public bool skipInWorkflow
        {
            get => _skipInWorkflow;
            set { _skipInWorkflow = value; Changed(nameof(skipInWorkflow)); }
        }
        private void Changed(string property)
        {
            DataChanged?.Invoke(0, EventArgs.Empty);
            OnPropertyChanged(property);
        }
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
            if (propertyName == nameof(hex) || propertyName == nameof(appendCrlf) ||
                propertyName == nameof(disableSuggestion) || propertyName == nameof(recvScriptPath) ||
                propertyName == nameof(recvScriptPara) || propertyName == nameof(responseMode) ||
                propertyName == nameof(expectedResponse) || propertyName == nameof(responseTimeoutMs) ||
                propertyName == nameof(responseRetries) || propertyName == nameof(skipInWorkflow))
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomOptions)));
        }
    }
}
