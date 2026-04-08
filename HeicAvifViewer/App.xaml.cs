using Microsoft.UI.Xaml;

namespace HeicAvifViewer;

/// <summary>
/// Application entry point. Initialises the WinUI 3 window and handles
/// activated file-association launches (e.g. double-clicking a .heic file).
/// </summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();

        // When the app is launched via a file association, the activation
        // arguments carry the file path(s). Forward them to the main window.
        var activationArgs = Microsoft.Windows.AppLifecycle.AppInstance
            .GetCurrent()
            .GetActivatedEventArgs();

        _mainWindow.Activate();

        if (activationArgs?.Kind ==
            Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
        {
            var fileArgs = activationArgs.Data as
                Windows.ApplicationModel.Activation.IFileActivatedEventArgs;

            if (fileArgs?.Files.Count > 0 &&
                fileArgs.Files[0] is Windows.Storage.StorageFile file)
            {
                _mainWindow.OpenFileOnLaunch(file.Path);
            }
        }
    }
}
