using System.Collections.Generic;
using System.ComponentModel;

namespace CookieJar.AppModel
{
    /// <summary>Base for view-models. Raises PropertyChanged only when a value really changes, so bindings do not churn.</summary>
    public abstract class ObservableBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
