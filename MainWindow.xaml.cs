using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BackupVerify;

public partial class MainWindow : Window
{
    private Settings? settings;
    private CheckMode mode = CheckMode.Quick;
    private CancellationTokenSource? cancellation;
    private CheckResult? lastResult;
    private Volume[] volumes = [];
    private string configurationStatus = "";
    private bool initializing = true;
    private bool IsBusy => cancellation is not null;
    private record Card(string Name, string Role, string Icon, string Status, string Summary, string Color);
    private record DetailRow(string Origin, string Group, string Path, string Detail);

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        ApplyMode();
        LoadSettings();
        initializing = false;
        RenderCards();
    }

    private static string NameOf(Source source) => source switch
    {
        Source.Nas => "NAS",
        Source.Usb => "USB NTFS",
        _ => "OneDrive"
    };

    private static string NameOf(CheckMode value) => value switch
    {
        CheckMode.Quick => "Rápida",
        CheckMode.Complete => "Completa",
        CheckMode.Selected => "Archivos elegidos",
        _ => "Aleatorio"
    };

    private Volume? SelectedVolume => volumes.FirstOrDefault(volume => volume.Id == settings?.UsbVolumeId);

    private void LoadSettings()
    {
        try
        {
            settings = Settings.Load();
            configurationStatus = $"{settings.BackupSets.Length} grupos · {settings.BackupSets.Sum(set => set.TestFiles.Length)} archivos elegidos";
        }
        catch (Exception error) { settings = null; configurationStatus = error.Message; }
        Scope.ItemsSource = new object[] { "Todos" }.Concat(settings?.BackupSets ?? []).ToArray();
        Scope.SelectedIndex = 0;
        ConfigStatus.Text = configurationStatus;
        Footer.Text = settings is null ? "Revisa config.json" : "";
        RefreshVolumes();
        ResetResult();
    }

    private void RefreshVolumes()
    {
        volumes = Volume.Connected();
        Drives.ItemsSource = volumes;
        Drives.SelectedItem = SelectedVolume;
        DriveStatus.Text = SelectedVolume is { } volume ? $"Disco vinculado · {volume}" :
            string.IsNullOrEmpty(settings?.UsbVolumeId) ? "Selecciona el volumen NTFS del backup." : "Disco vinculado desconectado";
    }

    private void RenderCards()
    {
        Cards.ItemsSource = Enum.GetValues<Source>().Select(source =>
        {
            var result = lastResult?.Sources.Single(item => item.Source == source);
            var status = result?.Status ?? (source == Source.Usb
                ? SelectedVolume is not null ? "Conectado" : string.IsNullOrEmpty(settings?.UsbVolumeId) ? "Sin vincular" : "Desconectado"
                : "Sin verificar");
            var selected = Scope.SelectedItem as BackupSet;
            var summary = result is { Checked: > 0 } || result is { Files: > 0 } || result is { Folders: > 0 }
                ? mode is CheckMode.Selected or CheckMode.Random ? $"{result.Files:N0} recuperados"
                    : $"{result.Files:N0} archivos · {result.Folders:N0} carpetas"
                : source switch
                {
                    Source.Nas => selected?.NasPath ?? "Recursos SMB",
                    Source.Usb => SelectedVolume?.ToString() ?? selected?.UsbPath ?? "Disco externo",
                    _ => selected?.CloudPath ?? "rclone crypt"
                };
            return new Card(NameOf(source),
                source switch { Source.Nas => "Original", Source.Usb => "Local", _ => "Fuera de casa" },
                source switch { Source.Nas => "\uEDA2", Source.Usb => "\uEDA5", _ => "\uE753" },
                status, summary,
                status switch { "Correcto" or "Conectado" => "#6DE6BA", "Error" => "#FF9E9E",
                    "Diferencias" or "No disponible" or "Desconectado" => "#F7CB83", _ => "#A7B8CA" });
        }).ToArray();
    }

    private void ApplyMode()
    {
        foreach (var button in new[] { QuickButton, CompleteButton, SelectedButton, RandomButton })
        {
            var active = (string)button.Tag == mode.ToString();
            button.Background = (Brush)FindResource(active ? "Accent" : "Panel");
            button.Foreground = active ? Brushes.Black : (Brush)FindResource("Text");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
        ModeHint.Text = mode switch
        {
            CheckMode.Quick => "Rutas, tamaños y recuentos.",
            CheckMode.Complete => "Contenido del USB y OneDrive. Lee todos los archivos del NAS.",
            CheckMode.Selected => "Recupera los archivos indicados en config.json.",
            _ => "Elige y recupera un archivo del NAS al azar."
        };
    }

    private void SelectMode(object sender, RoutedEventArgs args)
    {
        mode = Enum.Parse<CheckMode>((string)((Button)sender).Tag);
        ApplyMode();
        ResetResult();
    }

    private void ScopeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initializing) ResetResult();
    }

    private void ResetResult()
    {
        lastResult = null;
        DetailsButton.Visibility = Visibility.Collapsed;
        RunStatus.Text = "";
        RunStatus.ToolTip = null;
        VerifyButton.IsEnabled = settings is not null && !IsBusy;
        RenderCards();
    }

    private async void Verify(object sender, RoutedEventArgs args)
    {
        if (IsBusy || settings is not { } current) return;
        RefreshVolumes();
        ResetResult();
        var sets = Scope.SelectedItem is BackupSet set ? new[] { set } : current.BackupSets;
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        SetBusy(true);
        RunStatus.Text = "Verificando…";
        var progress = new Progress<string>(text =>
        {
            if (ReferenceEquals(cancellation, operation)) RunStatus.Text = text;
        });
        try
        {
            var verification = new Verification(current, SelectedVolume?.Root);
            var result = await Task.Run(() => verification.Run(mode, sets, progress, operation.Token));
            Present(result);
        }
        catch (OperationCanceledException) { RunStatus.Text = "Cancelada"; }
        catch (Exception error)
        {
            RunStatus.Text = "Error";
            ShowError(error);
        }
        finally
        {
            cancellation = null;
            SetBusy(false);
        }
    }

    private void Present(CheckResult result)
    {
        lastResult = result;
        DetailsButton.Visibility = Visibility.Visible;
        var problems = result.Findings.Count(item => item.Kind != FindingKind.Recovered);
        RunStatus.Text = result.Sample ?? (problems switch
        {
            0 => "Comprobación terminada",
            1 => "1 incidencia",
            _ => $"{problems:N0} incidencias"
        });
        RunStatus.ToolTip = RunStatus.Text;
        RenderCards();
    }

    private void SetBusy(bool running)
    {
        Scope.IsEnabled = QuickButton.IsEnabled = CompleteButton.IsEnabled = SelectedButton.IsEnabled =
            RandomButton.IsEnabled = !running;
        VerifyButton.IsEnabled = !running && settings is not null;
        Activity.Visibility = CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Activity.IsIndeterminate = running;
    }

    private void Cancel(object sender, RoutedEventArgs args) => cancellation?.Cancel();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs args)
    {
        if (IsBusy)
        {
            cancellation?.Cancel();
            args.Cancel = true;
        }
        base.OnClosing(args);
    }

    private void ShowPanel(FrameworkElement panel)
    {
        foreach (var item in new FrameworkElement[] { Dashboard, Details, Configuration })
            item.Visibility = ReferenceEquals(item, panel) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDetails()
    {
        if (lastResult is not { } result) return;
        DetailsTitle.Text = $"{NameOf(mode)} · Resultado";
        Sample.Text = result.Sample ?? "";
        Sample.Visibility = result.Sample is null ? Visibility.Collapsed : Visibility.Visible;
        OutputButton.Visibility = result.Output is null ? Visibility.Collapsed : Visibility.Visible;
        Findings.ItemsSource = result.Findings.Select(item => new DetailRow(NameOf(item.Source), item.Group, item.Path, item.Detail)).ToArray();
        Findings.Visibility = result.Findings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowPanel(Details);
    }

    private void OpenDetails(object sender, RoutedEventArgs args) => ShowDetails();
    private void Back(object sender, RoutedEventArgs args) => ShowPanel(Dashboard);

    private void OpenSettings(object sender, RoutedEventArgs args)
    {
        if (IsBusy) return;
        RefreshVolumes();
        ShowPanel(Configuration);
    }

    private void RefreshDrives(object sender, RoutedEventArgs args) => RefreshVolumes();
    private void ReloadConfig(object sender, RoutedEventArgs args) => LoadSettings();
    private void OpenConfig(object sender, RoutedEventArgs args) => OpenPath(Settings.ConfigPath);

    private void SaveDrive(object sender, RoutedEventArgs args)
    {
        if (settings is null || Drives.SelectedItem is not Volume drive) return;
        try
        {
            var edited = Settings.Load() with { UsbVolumeId = drive.Id };
            edited.Save();
            LoadSettings();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OpenOutput(object sender, RoutedEventArgs args)
    {
        if (lastResult?.Output is { } output) OpenPath(output);
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error) => MessageBox.Show(this, error.Message, "Backup Verify", MessageBoxButton.OK, MessageBoxImage.Error);

    internal void CapturePreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        settings = new Settings("demo", "rclone.conf",
        [
            new BackupSet("Fotos", @"\\nas\fotos", "Backup NAS a HDD/Fotos", "onedrive-crypt:Fotos", ["2025/viaje.jpg"]),
            new BackupSet("PC", @"\\nas\pc", "Backup NAS a HDD/PC", "onedrive-crypt:PC", []),
            new BackupSet("Otros backups", @"\\nas\otros backups", "Backup NAS a HDD/Otros backups", "onedrive-crypt:Otros backups", [])
        ]);
        Scope.ItemsSource = new object[] { "Todos" }.Concat(settings.BackupSets).ToArray();
        Scope.SelectedIndex = 0;
        ConfigStatus.Text = "3 grupos · 1 archivo elegido";
        Footer.Text = "";
        volumes = [new Volume(@"E:\", "BACKUP HDD", "demo")];
        ResetResult();
        Capture("01-verificacion.png");

        mode = CheckMode.Complete;
        ApplyMode();
        Present(new CheckResult(
            [new(Source.Nas, 12480, 462, 3, 0, 0, 0), new(Source.Usb, 12480, 462, 3, 0, 0, 0), new(Source.Cloud, 12479, 462, 3, 1, 0, 0)],
            [new(Source.Cloud, FindingKind.Difference, "Fotos", "2025/viaje.jpg", "Archivo faltante")], null, null));
        Capture("02-resumen.png");
        ShowDetails();
        Capture("03-detalle.png");

        mode = CheckMode.Random;
        Present(new CheckResult(
            [new(Source.Nas, 1, 0, 1, 0, 0, 0), new(Source.Usb, 1, 0, 1, 0, 0, 0), new(Source.Cloud, 1, 0, 1, 0, 0, 0)],
            [new(Source.Nas, FindingKind.Recovered, "Fotos", "2025/viaje.jpg", "viaje_nas.jpg"),
             new(Source.Usb, FindingKind.Recovered, "Fotos", "2025/viaje.jpg", "viaje_usb.jpg"),
             new(Source.Cloud, FindingKind.Recovered, "Fotos", "2025/viaje.jpg", "viaje_cloud.jpg")], "checks", "Fotos / 2025/viaje.jpg"));
        ShowDetails();
        Capture("04-recuperacion.png");

        Drives.ItemsSource = volumes;
        Drives.SelectedItem = SelectedVolume;
        DriveStatus.Text = "Disco vinculado · E:  BACKUP HDD";
        ShowPanel(Configuration);
        Capture("05-configuracion.png");

        void Capture(string name)
        {
            Surface.UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Surface.ActualWidth, (int)Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name));
            encoder.Save(file);
        }
    }
}
