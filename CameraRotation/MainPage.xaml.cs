using Camera.MAUI;
using Microsoft.Maui.Controls.Shapes;
using System.Diagnostics;

namespace CameraRotation
{
    public partial class MainPage : ContentPage
    {
        private bool _cameraStarted;
        private bool _usingHardwareZoom;
        private bool _torchOn;
        private double _softwareZoom = 1.0;
        private const double SoftwareZoomMin = 1.0;
        private const double SoftwareZoomMax = 4.0;

        private bool _isStopping;
        private bool _isCameraRunning;
        private readonly SemaphoreSlim _cameraLock = new(1, 1);

        public MainPage()
        {
            InitializeComponent();

            InternalCamera.CamerasLoaded += OnCamerasLoaded;
            SizeChanged += OnPageSizeChanged;
            DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;

            ConfigureInitialLayout();
            ClipPreviewToBounds();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            var status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Permission needed", "Camera permission is required.", "OK");
                return;
            }
        }
        protected override async void OnDisappearing()
        {
            DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
            InternalCamera.CamerasLoaded -= OnCamerasLoaded;
            SizeChanged -= OnPageSizeChanged;

            if (_cameraStarted)
            {
                try
                {
                    await StopCameraSafeAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }

            base.OnDisappearing();
        }

        private async void OnCamerasLoaded(object? sender, EventArgs e)
        {
            if (InternalCamera.Cameras.Count == 0)
            {
                await DisplayAlert("Camera", "No camera detected.", "OK");
                return;
            }

            InternalCamera.Camera = InternalCamera.Cameras.First();
            InternalCamera.IsVisible = InternalCamera.Camera?.HasFlashUnit == true;

            var minZoom = InternalCamera.Camera?.MinZoomFactor ?? 1f;
            var maxZoom = InternalCamera.Camera?.MaxZoomFactor ?? 1f;

            _usingHardwareZoom = maxZoom > minZoom && maxZoom > 1.01f;

            ZoomSlider.Minimum = 1;
            ZoomSlider.Maximum = _usingHardwareZoom ? maxZoom : SoftwareZoomMax;
            ZoomSlider.Value = 1;
            ZoomLabel.Text = "Zoom: 1.0x";

            try
            {
                
                await StartCameraSafeAsync();
               
            }
            catch (Exception ex)
            {
                await DisplayAlert("Camera", ex.Message, "OK");
            }
        }

        private void OnZoomSliderChanged(object? sender, ValueChangedEventArgs e)
        {
            var zoomValue = Math.Round(e.NewValue, 2);
            ZoomLabel.Text = $"Zoom: {zoomValue:0.0}x";

            if (_usingHardwareZoom)
            {
                InternalCamera.ZoomFactor = (float)zoomValue;
                ResetSoftwareZoom();
                return;
            }

            ApplySoftwareZoom(zoomValue);
        }

        private void ApplySoftwareZoom(double zoom)
        {
            _softwareZoom = Math.Clamp(zoom, SoftwareZoomMin, SoftwareZoomMax);
            InternalPreview.Scale = _softwareZoom;
            InternalPreview.TranslationX = 0;
            InternalPreview.TranslationY = 0;
        }

        private void ResetSoftwareZoom()
        {
            _softwareZoom = 1.0;
            InternalPreview.Scale = 1.0;
            InternalPreview.TranslationX = 0;
            InternalPreview.TranslationY = 0;
        }

        private async void OnTakePhotoClicked(object? sender, EventArgs e)
        {
            if (!_cameraStarted)
                return;

            try
            {

                var stream = await InternalCamera.TakePhotoAsync();
                if (stream is null)
                {
                    await DisplayAlert("Photo", "Unable to capture photo.", "OK");
                    return;
                }

                var bytes = await ReadAllBytesAsync(stream);
                InternalPreview.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
                InternalPreview.IsVisible = true;
            }
            catch (Exception ex)
            {
                await DisplayAlert("Photo", ex.Message, "OK");
            }
            
        }

        private void OnTorchClicked(object? sender, EventArgs e)
        {
            _torchOn = !_torchOn;
            InternalCamera.TorchEnabled = _torchOn;
            TorchButton.Text = _torchOn ? "Torch On" : "Torch";
        }


        private async void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
        {
            try
            {
                await StartCameraSafeAsync();

                MainThread.BeginInvokeOnMainThread(ConfigureResponsiveLayout);

                await StopCameraSafeAsync();

            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                throw;
            }
           
        }

        private void OnPageSizeChanged(object? sender, EventArgs e)
        {
            
            ConfigureResponsiveLayout();
            ClipPreviewToBounds();
        }

        private void ConfigureInitialLayout()
        {
            ConfigureResponsiveLayout();
        }

        private void ConfigureResponsiveLayout()
        {
            if (Width <= 0 || Height <= 0)
                return;

            var isLandscape = Width > Height;

        }

        private void ClipPreviewToBounds()
        {
            if (PreviewViewport.Width <= 0 || PreviewViewport.Height <= 0)
                return;

            PreviewViewport.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, PreviewViewport.Width, PreviewViewport.Height)
            };
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            if (stream.CanSeek)
                stream.Position = 0;

            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return memory.ToArray();
        }

        private async Task StartCameraSafeAsync()
        {
            await _cameraLock.WaitAsync();
            try
            {
                if (_isCameraRunning || _isStopping)
                    return;

                var result = await InternalCamera.StartCameraAsync();
                _isCameraRunning = result == CameraResult.Success;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Start camera failed: {ex}");
            }
            finally
            {
                _cameraLock.Release();
            }
        }

        private async Task StopCameraSafeAsync()
        {
            await _cameraLock.WaitAsync();
            try
            {
                if (!_isCameraRunning || _isStopping)
                    return;

                _isStopping = true;

                // Small delay helps Android finish pending camera callbacks
                await Task.Delay(150);

                try
                {
                    await InternalCamera.StopCameraAsync();
                }
                catch (Java.Lang.IllegalStateException ex) when (ex.Message?.Contains("already closed") == true)
                {
                    System.Diagnostics.Debug.WriteLine("Camera already closed, ignoring.");
                }
                catch (ObjectDisposedException)
                {
                    System.Diagnostics.Debug.WriteLine("Camera already disposed, ignoring.");
                }

                _isCameraRunning = false;
            }
            finally
            {
                _isStopping = false;
                _cameraLock.Release();
            }
        }
    }

}
