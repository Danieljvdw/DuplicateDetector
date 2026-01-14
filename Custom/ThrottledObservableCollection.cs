using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;

namespace DuplicateDetector.Custom;

public class ThrottledObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;
    private DispatcherTimer? _timer;

    public TimeSpan ThrottleInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public void BeginSuppressNotifications()
    {
        _suppressNotifications = true;

        _timer = new DispatcherTimer(ThrottleInterval, DispatcherPriority.Background, (s, e) =>
        {
            FlushNotifications();
        }, Dispatcher.CurrentDispatcher);

        _timer.Start();
    }

    public void EndSuppressNotifications()
    {
        _timer?.Stop();
        _timer = null;
        FlushNotifications();
        _suppressNotifications = false;
    }

    private void FlushNotifications()
    {
        // Trigger a Reset event to refresh the UI
        if (_suppressNotifications)
        {
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnCollectionChanged(e);
        // else ignore until FlushNotifications fires
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnPropertyChanged(e);
        // else ignore until FlushNotifications fires
    }
}
