using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NaiwaProxy.Models;
using NaiwaProxy.Services;

namespace NaiwaProxy;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly CoreService _coreService = new();
    private readonly ObservableCollection<VmessProfile> _profiles = [];
    private AppSettings _settings = new();
    private VmessProfile? _selectedProfile;
    private CancellationTokenSource? _latencyTestCancellation;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        Closed += (_, _) =>
        {
            _latencyTestCancellation?.Cancel();
            _latencyTestCancellation?.Dispose();
            _coreService.Stop();
        };
    }

    private void LoadSettings()
    {
        _settings = _settingsStore.Load();
        _profiles.Clear();
        foreach (var profile in _settings.Profiles)
        {
            _profiles.Add(profile);
        }

        ProfilesGrid.ItemsSource = _profiles;
        SocksPortBox.Text = _settings.SocksPort.ToString();
        HttpPortBox.Text = _settings.HttpPort.ToString();
        CoreExeBox.Text = _settings.CoreExecutable;

        SelectCombo(SecurityBox, "auto");
        SelectCombo(NetworkBox, "tcp");
        SelectCombo(TlsBox, "none");

        var selected = _profiles.FirstOrDefault(p => p.Id == _settings.SelectedProfileId) ?? _profiles.FirstOrDefault();
        ProfilesGrid.SelectedItem = selected;
        if (selected is not null)
        {
            LoadProfileToForm(selected);
        }

        UpdateStatus("Ready");
    }

    private void ProfilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is VmessProfile profile)
        {
            LoadProfileToForm(profile);
            _settings.SelectedProfileId = profile.Id;
            _settingsStore.Save(_settings);
        }
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProfileToForm(new VmessProfile());
        ProfilesGrid.SelectedItem = null;
        UpdateStatus("Editing a new profile");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ReadProfileFromForm();
            var existing = _profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (existing is null)
            {
                _profiles.Add(profile);
            }
            else
            {
                CopyProfile(profile, existing);
                existing.ResetLatency();
            }

            SaveProfiles(profile.Id);
            ProfilesGrid.Items.Refresh();
            ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(p => p.Id == profile.Id);
            UpdateStatus("Profile saved");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VmessProfile profile)
        {
            return;
        }

        _profiles.Remove(profile);
        SaveProfiles(_profiles.FirstOrDefault()?.Id);
        ProfilesGrid.SelectedItem = _profiles.FirstOrDefault();
        UpdateStatus("Profile deleted");
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var imported = VmessLinkParser.Parse(ImportBox.Text);
            _profiles.Add(imported);
            SaveProfiles(imported.Id);
            ProfilesGrid.SelectedItem = imported;
            ImportBox.Clear();
            UpdateStatus("VMess profile imported");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void SaveRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyRuntimeSettingsFromForm();
            SaveProfiles(_settings.SelectedProfileId);
            UpdateStatus("Runtime settings saved");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyRuntimeSettingsFromForm();
            var profile = GetSelectedProfile();
            _coreService.Start(_settings, profile);
            SaveProfiles(profile.Id);
            UpdateStatus($"Running: {profile.DisplayName}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _coreService.Stop();
        UpdateStatus("Stopped");
    }

    private void EnableProxyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyRuntimeSettingsFromForm();
            SystemProxyService.EnableHttpProxy(_settings.HttpPort);
            SaveProfiles(_settings.SelectedProfileId);
            UpdateStatus($"Windows HTTP proxy enabled on 127.0.0.1:{_settings.HttpPort}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void DisableProxyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SystemProxyService.DisableProxy();
            UpdateStatus("Windows proxy disabled");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void TestTcpLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VmessProfile profile)
        {
            UpdateStatus("Select a profile to test TCP latency");
            return;
        }

        await RunTcpLatencyTestsAsync([profile], parallel: true);
    }

    private async void TestRealLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VmessProfile profile)
        {
            UpdateStatus("Select a profile to test real latency");
            return;
        }

        try
        {
            ApplyRuntimeSettingsFromForm();
            await RunRealLatencyTestsAsync([profile]);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void TestAllTcpLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            UpdateStatus("No profiles to test");
            return;
        }

        await RunTcpLatencyTestsAsync(_profiles.ToList(), parallel: true);
    }

    private async void TestAllRealLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            UpdateStatus("No profiles to test");
            return;
        }

        try
        {
            ApplyRuntimeSettingsFromForm();
            await RunRealLatencyTestsAsync(_profiles.ToList());
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task RunTcpLatencyTestsAsync(IReadOnlyList<VmessProfile> profiles, bool parallel)
    {
        _latencyTestCancellation?.Cancel();
        _latencyTestCancellation?.Dispose();
        _latencyTestCancellation = new CancellationTokenSource();
        var cancellationToken = _latencyTestCancellation.Token;

        SetLatencyTestingEnabled(false);
        UpdateStatus($"Testing TCP latency for {profiles.Count} profile(s)...");

        try
        {
            if (parallel)
            {
                var tasks = profiles.Select(profile => TestTcpLatencyAsync(profile, cancellationToken));
                await Task.WhenAll(tasks);
            }
            else
            {
                foreach (var profile in profiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await TestTcpLatencyAsync(profile, cancellationToken);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                UpdateStatus("TCP latency test finished");
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("TCP latency test cancelled");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetLatencyTestingEnabled(true);
        }
    }

    private async Task RunRealLatencyTestsAsync(IReadOnlyList<VmessProfile> profiles)
    {
        _latencyTestCancellation?.Cancel();
        _latencyTestCancellation?.Dispose();
        _latencyTestCancellation = new CancellationTokenSource();
        var cancellationToken = _latencyTestCancellation.Token;

        SetLatencyTestingEnabled(false);
        UpdateStatus($"Testing real latency for {profiles.Count} profile(s)...");

        try
        {
            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TestRealLatencyAsync(profile, cancellationToken);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                UpdateStatus("Real latency test finished");
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Real latency test cancelled");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetLatencyTestingEnabled(true);
        }
    }

    private static async Task TestTcpLatencyAsync(VmessProfile profile, CancellationToken cancellationToken)
    {
        profile.BeginTcpLatencyTest();

        var latency = await LatencyTestService.MeasureTcpAsync(profile.Address, profile.Port, cancellationToken: cancellationToken);
        if (!cancellationToken.IsCancellationRequested)
        {
            profile.CompleteTcpLatencyTest(latency);
        }
    }

    private async Task TestRealLatencyAsync(VmessProfile profile, CancellationToken cancellationToken)
    {
        profile.BeginRealLatencyTest();

        var latency = await ProxyLatencyTestService.MeasureRealAsync(_settings, profile, cancellationToken);
        if (!cancellationToken.IsCancellationRequested)
        {
            profile.CompleteRealLatencyTest(latency);
        }
    }

    private void SetLatencyTestingEnabled(bool enabled)
    {
        TestTcpLatencyButton.IsEnabled = enabled;
        TestRealLatencyButton.IsEnabled = enabled;
        TestAllTcpLatencyButton.IsEnabled = enabled;
        TestAllRealLatencyButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
    }

    private void LoadProfileToForm(VmessProfile profile)
    {
        _selectedProfile = profile;
        NameBox.Text = profile.Name;
        AddressBox.Text = profile.Address;
        PortBox.Text = profile.Port.ToString();
        UuidBox.Text = profile.UserId;
        AlterIdBox.Text = profile.AlterId.ToString();
        HostBox.Text = profile.Host;
        SniBox.Text = profile.Sni;
        PathBox.Text = profile.Path;
        SelectCombo(SecurityBox, profile.Security);
        SelectCombo(NetworkBox, profile.Network);
        SelectCombo(TlsBox, string.IsNullOrWhiteSpace(profile.Tls) ? "none" : profile.Tls);
    }

    private VmessProfile ReadProfileFromForm()
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        if (!int.TryParse(AlterIdBox.Text, out var alterId) || alterId < 0)
        {
            throw new InvalidOperationException("Alter ID must be a non-negative number.");
        }

        if (string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            throw new InvalidOperationException("Address is required.");
        }

        if (!Guid.TryParse(UuidBox.Text.Trim(), out _))
        {
            throw new InvalidOperationException("UUID must be a valid VMess user id.");
        }

        return new VmessProfile
        {
            Id = _selectedProfile?.Id ?? Guid.NewGuid().ToString("N"),
            Name = NameBox.Text.Trim(),
            Address = AddressBox.Text.Trim(),
            Port = port,
            UserId = UuidBox.Text.Trim(),
            AlterId = alterId,
            Security = SelectedComboValue(SecurityBox, "auto"),
            Network = SelectedComboValue(NetworkBox, "tcp"),
            Tls = SelectedComboValue(TlsBox, "none") == "tls" ? "tls" : "",
            Host = HostBox.Text.Trim(),
            Sni = SniBox.Text.Trim(),
            Path = PathBox.Text.Trim()
        };
    }

    private VmessProfile GetSelectedProfile()
    {
        return ProfilesGrid.SelectedItem as VmessProfile
            ?? throw new InvalidOperationException("Select a VMess profile first.");
    }

    private void ApplyRuntimeSettingsFromForm()
    {
        if (!int.TryParse(SocksPortBox.Text, out var socksPort) || socksPort is <= 0 or > 65535)
        {
            throw new InvalidOperationException("SOCKS port must be between 1 and 65535.");
        }

        if (!int.TryParse(HttpPortBox.Text, out var httpPort) || httpPort is <= 0 or > 65535)
        {
            throw new InvalidOperationException("HTTP port must be between 1 and 65535.");
        }

        _settings.SocksPort = socksPort;
        _settings.HttpPort = httpPort;
        _settings.CoreExecutable = string.IsNullOrWhiteSpace(CoreExeBox.Text) ? "xray.exe" : CoreExeBox.Text.Trim();
    }

    private void SaveProfiles(string? selectedProfileId)
    {
        _settings.Profiles = _profiles.ToList();
        _settings.SelectedProfileId = selectedProfileId;
        _settingsStore.Save(_settings);
    }

    private static void CopyProfile(VmessProfile source, VmessProfile target)
    {
        target.Name = source.Name;
        target.Address = source.Address;
        target.Port = source.Port;
        target.UserId = source.UserId;
        target.AlterId = source.AlterId;
        target.Security = source.Security;
        target.Network = source.Network;
        target.Type = source.Type;
        target.Host = source.Host;
        target.Path = source.Path;
        target.Tls = source.Tls;
        target.Sni = source.Sni;
        target.Remark = source.Remark;
    }

    private static void SelectCombo(ComboBox comboBox, string value)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string SelectedComboValue(ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
    }

    private void UpdateStatus(string status)
    {
        StatusText.Text = status;
    }

    private static void ShowError(Exception exception)
    {
        MessageBox.Show(exception.Message, "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
