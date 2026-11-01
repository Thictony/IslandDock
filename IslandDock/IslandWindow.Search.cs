using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace IslandDock;

// The navbar's search bar, laid out like Windows search: Best match, then Apps, Settings, Documents and the web.
public partial class IslandWindow
{
    const int MaxApps = 6, MaxSettings = 4, MaxFiles = 8;
    DispatcherTimer? searchDelay;
    CancellationTokenSource? searchCts;
    /// <summary>Enter was pressed before the results came in; open the top one when they do.</summary>
    bool openFirst;

    void SearchBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // The box may still have focus from before the island collapsed, so focusing alone won't reopen the results.
        OpenSearch();
        if (!SearchBox.IsKeyboardFocusWithin)
        {
            SearchBox.Focusable = true;
            Keyboard.Focus(SearchBox);
            // Clicks on the bar's padding or icon shouldn't go on to anything else; clicks in the text place the caret.
            if (!SearchBox.IsMouseOver && !SearchClear.IsMouseOver) e.Handled = true;
        }
    }

    void SearchBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenSearch();

    // The box only takes focus when clicked; otherwise WPF hands it the keyboard whenever the island activates,
    // and hovering the island would open search.
    void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SearchBox.Focusable = false;

    void OpenSearch()
    {
        if (searchOpen) return;
        searchOpen = true;
        pickerOpen = mixerOpen = trayOpen = false;
        SearchSources.Warm();
        Activate();
        RunSearch();
    }

    void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        Keyboard.Focus(SearchBox);
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = SearchBox.Text.Length == 0;
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) OpenSearch();
        // Wait for a pause in typing rather than querying on every key.
        searchDelay ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) => RunSearch(), Dispatcher);
        searchDelay.Stop();
        searchDelay.Start();
    }

    async void RunSearch()
    {
        searchDelay?.Stop();
        searchCts?.Cancel();
        string text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            // Before typing, Windows search shows your apps; the taskbar's pins are the ones that matter here.
            var top = new List<SearchHit> { SearchHit.Header("Top apps") };
            top.AddRange(TaskbarPins.Current().Select(p => new SearchHit { Name = p.Title, Detail = "App", Launch = p.Launch, Icon = p.Icon }));
            SearchList.ItemsSource = top.Count > 1 ? top : null;
            SearchHint.Text = top.Count > 1 ? "" : "Type to search apps, settings, files and the web";
            return;
        }

        var cts = searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        SearchHint.Text = "";
        try
        {
            // A typed path or web address is opened directly, as Windows search does, rather than searched for.
            var (exact, places) = await Task.Run(() => SearchSources.Locations(text, 12), ct);
            if (ct.IsCancellationRequested) return;
            if (places.Count > 0)
            {
                var rows = new List<SearchHit>();
                if (exact) { rows.Add(SearchHit.Header("Best match")); rows.Add(places[0]); }
                var rest = exact ? places.Skip(1).ToList() : places;
                if (rest.Count > 0) { rows.Add(SearchHit.Header(exact ? "In this folder" : "Suggestions")); rows.AddRange(rest); }
                SearchList.ItemsSource = rows;
                SearchHint.Text = "";
                if (openFirst) { openFirst = false; OpenHit(rows.First(r => r.IsItem), reveal: false); }
                return;
            }

            // Apps and settings come back at once; files (the index) can take a moment, so show the rest first.
            await SearchSources.AppsReady(ct).ContinueWith(_ => { }, TaskScheduler.Default);
            var (apps, settings) = await Task.Run(() => (SearchSources.Apps(text, MaxApps), SearchSources.Settings(text, MaxSettings)), ct);
            if (ct.IsCancellationRequested) return;
            ShowResults(text, apps, settings, null);

            var files = await Task.Run(() => FileSearch.Find(text, MaxFiles, ct), ct);
            if (ct.IsCancellationRequested) return;
            ShowResults(text, apps, settings, files.Select(f => new SearchHit
            {
                Name = f.Name, Detail = f.Folder, Launch = f.Path, FilePath = f.Path, Icon = f.Icon,
            }).ToList());
        }
        catch (OperationCanceledException) { }
        catch { if (!ct.IsCancellationRequested) SearchHint.Text = "Search isn't available"; }
    }

    void ShowResults(string text, List<SearchHit> apps, List<SearchHit> settings, List<SearchHit>? files)
    {
        // The best match is a close app match (its name starts with what's typed), else a setting, else a file.
        SearchHit? best = apps.FirstOrDefault(a => SearchSources.Score(a.Name, "", text) <= 1)
            ?? settings.FirstOrDefault() ?? apps.FirstOrDefault() ?? files?.FirstOrDefault();

        var rows = new List<SearchHit>();
        void Section(string title, IEnumerable<SearchHit> hits)
        {
            var list = hits.Where(h => h != best).ToList();
            if (list.Count == 0) return;
            rows.Add(SearchHit.Header(title));
            rows.AddRange(list);
        }
        if (best != null) { rows.Add(SearchHit.Header("Best match")); rows.Add(best); }
        Section("Apps", apps);
        Section("Settings", settings);
        if (files != null) Section("Documents", files);
        rows.Add(SearchHit.Header("Search the web"));
        rows.Add(new SearchHit
        {
            Name = text, Detail = "See web results", Glyph = "",
            Launch = "https://www.bing.com/search?q=" + Uri.EscapeDataString(text),
        });
        SearchList.ItemsSource = rows;
        SearchHint.Text = files == null ? "" : files.Count == 0 && apps.Count == 0 && settings.Count == 0 ? "No results on this PC" : "";
        if (openFirst)
        {
            openFirst = false;
            OpenHit(rows.First(r => r.IsItem), reveal: false);
        }
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // Enter opens the top result; if a search is still pending, run it first and open when it lands.
            if (searchDelay?.IsEnabled == true && SearchBox.Text.Trim().Length > 0) { openFirst = true; RunSearch(); }
            else if (SearchList.ItemsSource is IEnumerable<SearchHit> rows && rows.FirstOrDefault(r => r.IsItem) is SearchHit hit) OpenHit(hit, reveal: false);
            e.Handled = true;
        }
    }

    void CloseSearch()
    {
        searchOpen = false;
        SearchBox.Clear();
        Keyboard.ClearFocus();
    }

    void SearchItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SearchHit hit) OpenHit(hit, reveal: false);
    }

    void SearchReveal_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SearchHit hit) OpenHit(hit, reveal: true);
        e.Handled = true;
    }

    void OpenHit(SearchHit hit, bool reveal)
    {
        CloseSearch();
        // Started the way a pin is, so the window it opens is docked as a tab; a pinned app pulses in its own place.
        if (!reveal && TaskbarPins.Current().FirstOrDefault(p => p.Launch.Equals(hit.Launch, StringComparison.OrdinalIgnoreCase)) is PinEntry pin)
        {
            LaunchPin(pin);
            return;
        }
        var entry = reveal && hit.FilePath != null
            ? new PinEntry("explorer.exe", null, null, hit.Name) { Arguments = $"/select,\"{hit.FilePath}\"", AnyNewWindow = true }
            : new PinEntry(hit.Launch, null, null, hit.Name) { AnyNewWindow = true };
        LaunchPin(entry);
    }
}
