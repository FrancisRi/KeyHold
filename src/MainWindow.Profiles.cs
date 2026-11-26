using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace KeyHoldMacro;

public sealed partial class MainWindow
{
    private readonly string profileFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyHoldMacro", "Profiles");
    private string currentPath, loadedName;

    private readonly List<string> profilePaths = new();

    private void MarkDirty()
    {
        if (loading) return;
        dirty = true;
        // TextChanging runs synchronously during both edits and profile loads.
        // Update the title after the text control leaves its change callback.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!disposed && dirty) UpdateProfileTitle();
        });
    }

    private void UpdateProfileTitle()
    {
        string subtitle = ProfileName.Text + (dirty ? " *" : "");
        Title = "KeyHold · " + subtitle;
        AppTitleBar.Subtitle = subtitle;
    }

    private Profile GetProfile()
    {
        var profile = new Profile
        {
            Name = ProfileName.Text.Trim(),
            SuppressKeys = SuppressKeys.IsChecked == true,
            PauseOnFocusLoss = PauseOnFocus.IsChecked == true
        };
        foreach (var row in rows)
        {
            Macro macro = row.Read();
            if (macro != null) profile.Macros.Add(macro);
        }
        profile.Validate();
        return profile;
    }

    private void LoadProfile(Profile profile, string path)
    {
        profile.Validate();
        loading = true;
        try
        {
            ProfileName.Text = profile.Name;
            SuppressKeys.IsChecked = profile.SuppressKeys;
            PauseOnFocus.IsChecked = profile.PauseOnFocusLoss;
            for (int i = 0; i < rows.Count; i++)
                rows[i].Load(i < profile.Macros.Count ? profile.Macros[i] : null);
            currentPath = path;
            loadedName = profile.Name;
            dirty = false;
            pendingPointRow = null;
            Picked.Text = "";
            UpdateProfileTitle();
            if (path != null) Remember(path);
        }
        finally { loading = false; }
    }

    private void RefreshProfiles()
    {
        SavedProfiles.Items.Clear();
        profilePaths.Clear();
        foreach (string path in Directory.GetFiles(profileFolder, "*.json"))
        {
            try
            {
                Profile profile = ProfileStore.Read(path);
                profilePaths.Add(path);
                SavedProfiles.Items.Add(profile.Name);
                if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                    SavedProfiles.SelectedIndex = profilePaths.Count - 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            {
                // Leave unreadable profiles on disk for recovery.
            }
        }
        if (SavedProfiles.SelectedIndex < 0 && SavedProfiles.Items.Count > 0)
            SavedProfiles.SelectedIndex = 0;
    }

    private async Task ReadSelected()
    {
        int index = SavedProfiles.SelectedIndex;
        if (index < 0) return;
        string path = profilePaths[index];
        if (!await DiscardOrSave()) return;
        LoadProfile(ProfileStore.Read(path), path);
        Status.Text = L.T("已读取配置：") + ProfileName.Text;
    }

    private async Task<bool> SaveProfile()
    {
        Profile profile = GetProfile();
        string path = currentPath;
        if (path == null || profile.Name != loadedName)
        {
            string filename = profile.Name;
            foreach (char invalid in Path.GetInvalidFileNameChars()) filename = filename.Replace(invalid, '_');
            path = Path.Combine(profileFolder, "profile_" + filename.Trim().TrimEnd('.') + ".json");
            if (File.Exists(path))
            {
                var result = await Ask("保存配置", "该名称对应的配置已存在，是否覆盖？", "覆盖");
                if (result != ContentDialogResult.Primary) return false;
            }
        }

        ProfileStore.Write(path, profile);
        currentPath = path;
        loadedName = profile.Name;
        dirty = false;
        Remember(path);
        RefreshProfiles();
        UpdateProfileTitle();
        Status.Text = L.T("配置已保存：") + profile.Name;
        return true;
    }

    private async Task<bool> DiscardOrSave()
    {
        if (!dirty) return true;
        var result = await Ask("未保存的配置", "当前配置有修改，是否先保存？", "保存配置", "放弃修改");
        if (result == ContentDialogResult.Primary) return await SaveProfile();
        return result == ContentDialogResult.Secondary;
    }

    private async Task<ContentDialogResult> Ask(string title, string content, string primary, string secondary = "")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Language = L.Language,
            Title = L.T(title),
            Content = L.T(content),
            PrimaryButtonText = L.T(primary),
            SecondaryButtonText = L.T(secondary),
            CloseButtonText = L.T("取消"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync();
    }

    private async Task Import()
    {
        if (!await DiscardOrSave()) return;
        var picker = new FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        var result = await picker.PickSingleFileAsync();
        if (result == null) return;
        LoadProfile(ProfileStore.Read(result.Path), null);
        MarkDirty();
        Status.Text = L.T("已导入；点击保存配置后可反复读取。");
    }

    private async Task Export()
    {
        Profile profile = GetProfile();
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "macro-profile" };
        picker.FileTypeChoices.Add(L.T("JSON 配置"), new[] { ".json" });
        var result = await picker.PickSaveFileAsync();
        if (result == null) return;
        ProfileStore.Write(result.Path, profile);
        Status.Text = L.T("配置已导出。");
    }

    private void Remember(string path)
    {
        try { File.WriteAllText(Path.Combine(profileFolder, "last-profile.txt"), Path.GetFileName(path)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void TryLoadLast()
    {
        try
        {
            string marker = Path.Combine(profileFolder, "last-profile.txt");
            if (!File.Exists(marker)) return;
            string file = File.ReadAllText(marker).Trim();
            if (Path.GetFileName(file) != file) return;
            string path = Path.Combine(profileFolder, file);
            if (!File.Exists(path)) return;
            LoadProfile(ProfileStore.Read(path), path);
            RefreshProfiles();
        }
        catch (Exception ex) { Status.Text = L.T("上次配置无法读取，请重新选择或导入：") + ex.Message; }
    }

    private async Task ChangeLanguage()
    {
        string requested = LanguageChoice.SelectedIndex == 1 ? "en" : "zh-CN";
        if (requested == L.Language) return;
        EmergencyStop(false);
        try
        {
            if (!await DiscardOrSave()) return;
            L.SavePreference(L.PreferencePath, requested);
            L.Language = requested;
            Localize();
            LoadProfile(new Profile(), null);
            RefreshProfiles();
            TryLoadLast();
        }
        catch (Exception ex) { ReportError(L.T("语言设置无法保存：") + ex.Message); }
        finally
        {
            loading = true;
            LanguageChoice.SelectedIndex = L.Language == "en" ? 1 : 0;
            loading = false;
        }
    }
}
