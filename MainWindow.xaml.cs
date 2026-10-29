using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FaceLockApp.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace FaceLockApp;

public class GalleryItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public BitmapSource? DisplaySource { get; set; }
    public string DateLabel { get; set; } = string.Empty;
}

public partial class MainWindow : System.Windows.Window
{
    private readonly SecurityService _securityService;
    private FaceEngine? _faceEngine;
    private PresenceMonitor? _monitor;
    private Mat? _latestRawFrame;
    private readonly object _frameLock = new();

    public ObservableCollection<GalleryItemViewModel> GalleryItems { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        _securityService = new SecurityService();
        GalleryListBox.ItemsSource = GalleryItems;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureAuthUi();
    }

    private void ConfigureAuthUi()
    {
        // When master password is not entered, ensure monitor is de-authenticated
        _monitor?.SetAuthenticated(false);

        AuthErrorText.Visibility = Visibility.Collapsed;
        PasswordInput.Clear();
        ConfirmPasswordInput.Clear();

        if (!_securityService.IsPasswordConfigured())
        {
            AuthSubtitle.Text = "First-time setup: Create a master password";
            ConfirmPasswordPanel.Visibility = Visibility.Visible;
            SubmitAuthBtn.Content = "Set Password & Proceed";
        }
        else
        {
            AuthSubtitle.Text = "Enter master password to access system";
            ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
            SubmitAuthBtn.Content = "Unlock Interface";
        }

        AuthGrid.Visibility = Visibility.Visible;
        DashboardGrid.Visibility = Visibility.Collapsed;
        PasswordInput.Focus();
    }

    private void SubmitAuthBtn_Click(object sender, RoutedEventArgs e)
    {
        HandleAuthentication();
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            HandleAuthentication();
        }
    }

    private void HandleAuthentication()
    {
        string password = PasswordInput.Password;

        if (!_securityService.IsPasswordConfigured())
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                ShowAuthError("Password cannot be blank.");
                return;
            }

            if (password != ConfirmPasswordInput.Password)
            {
                ShowAuthError("Passwords do not match.");
                return;
            }

            _securityService.SetPassword(password);
            OpenDashboard();
        }
        else
        {
            if (_securityService.ValidatePassword(password))
            {
                OpenDashboard();
            }
            else
            {
                ShowAuthError("Incorrect password. Please try again.");
                PasswordInput.SelectAll();
            }
        }
    }

    private void ShowAuthError(string msg)
    {
        AuthErrorText.Text = msg;
        AuthErrorText.Visibility = Visibility.Visible;
    }

    private void OpenDashboard()
    {
        AuthGrid.Visibility = Visibility.Collapsed;
        DashboardGrid.Visibility = Visibility.Visible;

        if (_faceEngine == null)
        {
            try
            {
                _faceEngine = new FaceEngine();
                _monitor = new PresenceMonitor(_faceEngine, _securityService);

                _monitor.FrameArrived += Monitor_FrameArrived;
                _monitor.StateChanged += Monitor_StateChanged;
                _monitor.StatusMessageChanged += Monitor_StatusMessageChanged;
                _monitor.ArmingCountdownTick += Monitor_ArmingCountdownTick;

                _monitor.StartCamera();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "Error initializing camera or face engine:\n" + ex.Message,
                    "Initialization Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;
            }
        }

        // Authenticate monitor
        if (_monitor == null) return;
        _monitor.SetAuthenticated(true);
        RefreshGalleryUi();

        // When logged in, 3-second grace period starts if photos exist
        if (_monitor.HasRegisteredOwner)
        {
            _monitor.StartArmingCountdown();
        }
        else
        {
            FooterStatusText.Text = "Please take at least one picture of your face to enable protection.";
        }
    }

    private void RefreshGalleryUi()
    {
        GalleryItems.Clear();
        if (_monitor == null) return;

        _monitor.ReloadGallery();
        var gallery = _monitor.Gallery;

        foreach (var sample in gallery.Samples)
        {
            BitmapSource? bmp = null;
            if (sample.DisplayImagePng.Length > 0)
            {
                using var ms = new MemoryStream(sample.DisplayImagePng);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
                bmp = image;
            }

            GalleryItems.Add(new GalleryItemViewModel
            {
                Id = sample.Id,
                DisplaySource = bmp,
                DateLabel = sample.CapturedAt.ToString("MM/dd HH:mm:ss")
            });
        }

        PhotoCountText.Text = $"{GalleryItems.Count} photo{(GalleryItems.Count == 1 ? "" : "s")}";
        DeleteSelectedBtn.IsEnabled = (GalleryListBox.SelectedItem != null);
    }

    private void Monitor_FrameArrived(object? sender, FrameEventArgs e)
    {
        lock (_frameLock)
        {
            _latestRawFrame?.Dispose();
            _latestRawFrame = e.Frame.Clone();
        }

        using var annotated = e.Frame.Clone();

        foreach (var face in e.DetectedFaces)
        {
            bool isOwner = (e.OwnerFace.HasValue && e.OwnerFace.Value == face);
            var color = isOwner ? Scalar.FromRgb(16, 185, 129) : Scalar.FromRgb(239, 68, 68);
            string label = isOwner ? $"Owner {(int)(e.MatchScore * 100)}%" : "Unknown";

            Cv2.Rectangle(annotated, face, color, thickness: 2);
            Cv2.PutText(
                annotated,
                label,
                new OpenCvSharp.Point(face.X, Math.Max(20, face.Y - 8)),
                HersheyFonts.HersheySimplex,
                0.6,
                color,
                thickness: 2
            );
        }

        var bitmapSource = annotated.ToWriteableBitmap();
        bitmapSource.Freeze();

        Dispatcher.InvokeAsync(() =>
        {
            CameraPreviewImage.Source = bitmapSource;
            CameraPlaceholder.Visibility = Visibility.Collapsed;

            if (e.DetectedFaces.Length > 0 && e.MatchScore > 0)
            {
                FooterConfidenceText.Text = $"Best Likeness: {(int)(e.MatchScore * 100)}%";
            }
            else
            {
                FooterConfidenceText.Text = "";
            }
        });
    }

    private void Monitor_ArmingCountdownTick(object? sender, int secondsRemaining)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (secondsRemaining > 0)
            {
                ArmingOverlay.Visibility = Visibility.Visible;
                CountdownNumberText.Text = secondsRemaining.ToString();
            }
            else
            {
                ArmingOverlay.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void Monitor_StateChanged(object? sender, MonitorState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            switch (state)
            {
                case MonitorState.Stopped:
                    StatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));
                    StatusBadgeText.Text = "Stopped";
                    ToggleMonitorBtn.Content = "▶ Start Protection (3s delay)";
                    ToggleMonitorBtn.Style = (Style)FindResource("ModernBtn");
                    ArmingOverlay.Visibility = Visibility.Collapsed;
                    break;

                case MonitorState.Arming:
                    StatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
                    StatusBadgeText.Text = "Arming...";
                    ToggleMonitorBtn.Content = "⏹ Stop Protection";
                    ToggleMonitorBtn.Style = (Style)FindResource("DangerBtn");
                    ArmingOverlay.Visibility = Visibility.Visible;
                    break;

                case MonitorState.ActiveMonitoring:
                    StatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                    StatusBadgeText.Text = "Active Protection";
                    ToggleMonitorBtn.Content = "⏹ Stop Protection";
                    ToggleMonitorBtn.Style = (Style)FindResource("DangerBtn");
                    ArmingOverlay.Visibility = Visibility.Collapsed;
                    break;

                case MonitorState.Triggered:
                    StatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                    StatusBadgeText.Text = "Workstation Locked";
                    break;
            }
        });
    }

    private void Monitor_StatusMessageChanged(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            FooterStatusText.Text = message;
        });
    }

    private void CaptureSampleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        Mat? currentMat = null;
        lock (_frameLock)
        {
            if (_latestRawFrame != null && !_latestRawFrame.Empty())
            {
                currentMat = _latestRawFrame.Clone();
            }
        }

        if (currentMat == null)
        {
            System.Windows.MessageBox.Show(
                "No camera frame available. Please ensure camera is connected.",
                "Capture Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        using (currentMat)
        {
            var (success, message, _) = _monitor.CaptureAndAddSample(currentMat);
            if (success)
            {
                RefreshGalleryUi();
                System.Windows.MessageBox.Show(
                    $"{message}\nTotal registered photos: {_monitor.Gallery.Samples.Count}\n\nSystem will re-arm with 3 seconds delay.",
                    "Photo Enrolled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                _monitor.StartArmingCountdown();
            }
            else
            {
                System.Windows.MessageBox.Show(message, "Capture Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void GalleryListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        DeleteSelectedBtn.IsEnabled = (GalleryListBox.SelectedItem != null);
    }

    private void DeleteSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        if (GalleryListBox.SelectedItem is GalleryItemViewModel selected)
        {
            var result = System.Windows.MessageBox.Show(
                "Are you sure you want to delete this registered face photo?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (result == MessageBoxResult.Yes)
            {
                _monitor.DeleteSample(selected.Id);
                RefreshGalleryUi();
            }
        }
    }

    private void ClearAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor == null || _monitor.Gallery.Samples.Count == 0) return;

        var result = System.Windows.MessageBox.Show(
            "Are you sure you want to remove ALL registered owner photos?\nThis will stop monitoring until a new photo is captured.",
            "Confirm Delete All",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (result == MessageBoxResult.Yes)
        {
            _securityService.SaveGallery(new OwnerGallery());
            _monitor.ReloadGallery();
            RefreshGalleryUi();
            _monitor.SetAuthenticated(true); // remain in UI but stopped
        }
    }

    private void ToggleMonitorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        if (_monitor.State == MonitorState.Stopped)
        {
            if (!_monitor.HasRegisteredOwner)
            {
                var result = System.Windows.MessageBox.Show(
                    "You have not registered any face photos yet.\nWould you like to capture your photo now?",
                    "No Photos Enrolled",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    CaptureSampleBtn_Click(sender, e);
                }
                return;
            }
            _monitor.StartArmingCountdown();
        }
        else
        {
            _monitor.SetAuthenticated(true);
            // Calling SetAuthenticated or stop
            _monitor.StartCamera();
            ConfigureAuthUi(); // or stop monitoring
        }
    }

    private void ActionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_monitor != null)
        {
            _monitor.LogoutInsteadOfLock = (ActionComboBox.SelectedIndex == 1);
        }
    }

    private void DelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int seconds = (int)e.NewValue;
        if (DelayLabel != null)
        {
            DelayLabel.Text = $"{seconds} seconds";
        }
        if (_monitor != null)
        {
            _monitor.LockDelaySeconds = seconds;
        }
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SensitivityLabel != null)
        {
            SensitivityLabel.Text = $"{(int)(e.NewValue * 100)}%";
        }
        if (_monitor != null)
        {
            _monitor.MatchThreshold = e.NewValue;
        }
    }

    private void LockUiBtn_Click(object sender, RoutedEventArgs e)
    {
        ConfigureAuthUi();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _monitor?.Dispose();
        _faceEngine?.Dispose();
        lock (_frameLock)
        {
            _latestRawFrame?.Dispose();
            _latestRawFrame = null;
        }
    }
}
