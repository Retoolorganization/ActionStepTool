using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ActionstepGet;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private IConfiguration? _configuration;
    private ActionstepClient? _client;
    private CancellationTokenSource? _operation;

    private bool _isBusy;
    private bool _isAuthed;
    private int _selectedEnvironmentIndex;
    private int _selectedFormatIndex;
    private int _renderVersion;
    private string _requestMethod = "GET";
    private string _requestBody = "";
    private string _rawBody = "";
    private string _jsonOutput = "";
    private string _yamlOutput = "";
    private string _jsonNotice = "";
    private string _yamlNotice = "";
    private string _statusText =
        "Log in, enter an endpoint, and run a GET request.";

    private bool _restoringFind;
    private bool _rendering;

    private IReadOnlyList<ResponseTable> _tables =
        Array.Empty<ResponseTable>();

    private ResponseTable? _selectedTable;

    private sealed record FindMatch(
        int Start,
        DataRowView? Row = null,
        string? Column = null);

    private sealed class FindState
    {
        public string Input = "";
        public string Query = "";
        public List<FindMatch> Matches = new();
        public int Index = -1;
    }

    private readonly FindState[] _findStates =
    {
        new(),
        new(),
        new()
    };

    public sealed class ResponseTable
    {
        public string Name { get; }
        public DataTable Data { get; } = new();

        public ResponseTable(string name)
        {
            Name = name;
        }
    }

    public string Endpoint { get; set; } =
        "/api/rest/actions?pageSize=200&page=1";

    public string[] RequestMethods { get; } = { "GET", "POST" };

    public string RequestMethod
    {
        get => _requestMethod;
        set
        {
            if (value is not ("GET" or "POST") ||
                value == _requestMethod)
            {
                return;
            }

            _requestMethod = value;
            Changed();
            Changed(nameof(IsPostRequest));
            Changed(nameof(CanFetchAllPages));
        }
    }

    public string RequestBody
    {
        get => _requestBody;
        set => _requestBody = value;
    }

    public bool IsPostRequest => RequestMethod == "POST";
    public bool CanFetchAllPages => IsIdle && RequestMethod == "GET";

    public bool FetchAllPages { get; set; }

    public bool IsAuthed => _isAuthed;
    public bool IsIdle => !_isBusy;
    public bool CanRun => _isAuthed && IsIdle;

    public string AuthButtonText =>
        _isAuthed ? "Log out" : "Log in";

    public string[] Environments { get; } =
        new[] { "Staging", "Production" };

    public string SelectedEnvironment =>
        Environments[_selectedEnvironmentIndex];

    public string JsonOutput => _jsonOutput;
    public string YamlOutput => _yamlOutput;

    public string Output =>
        SelectedFormatIndex == 1 ? YamlOutput : JsonOutput;

    public bool HasOutput =>
        SelectedFormatIndex == 2
            ? SelectedTable != null
            : !string.IsNullOrEmpty(Output);

    public string DisplayNotice => SelectedFormatIndex switch
    {
        1 => _yamlNotice,
        2 => Tables.Count == 0
            ? "No record collections in this response."
            : "",
        _ => _jsonNotice
    };

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            Changed();
        }
    }

    public IReadOnlyList<ResponseTable> Tables => _tables;

    public string TableRowCount =>
        SelectedTable == null
            ? ""
            : $"{SelectedTable.Data.Rows.Count} rows";

    public ResponseTable? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (ReferenceEquals(_selectedTable, value))
                return;

            _selectedTable = value;
            TableGrid.ItemsSource = value?.Data.DefaultView;

            Changed();
            Changed(nameof(TableRowCount));
            Changed(nameof(HasOutput));

            if (!_rendering)
                RebuildFind(2);
        }
    }

    public int SelectedEnvironmentIndex
    {
        get => _selectedEnvironmentIndex;
        set
        {
            if (value is < 0 or > 1 ||
                value == _selectedEnvironmentIndex)
            {
                return;
            }

            if (_isAuthed || _isBusy)
            {
                Changed();
                return;
            }

            _selectedEnvironmentIndex = value;
            Changed();
            Changed(nameof(SelectedEnvironment));

            if (_configuration != null)
                _ = ConnectEnvironmentAsync();
        }
    }

    public int SelectedFormatIndex
    {
        get => _selectedFormatIndex;
        set
        {
            if (value is < 0 or > 2 ||
                value == _selectedFormatIndex)
            {
                return;
            }

            _selectedFormatIndex = value;
            Changed();
            Changed(nameof(HasOutput));
            Changed(nameof(DisplayNotice));

            RestoreFindControls();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var state = _findStates[SelectedFormatIndex];

                if (state.Index >= 0 &&
                    state.Index < state.Matches.Count)
                {
                    ShowFindMatch(state.Index);
                }
            }));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        Loaded += MainWindow_Loaded;

        Closed += (_, _) =>
        {
            _operation?.Cancel();
            _client?.Dispose();
        };
    }

    private async void MainWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.actionstep.json", optional: true)
                .Build();

            _selectedEnvironmentIndex = string.Equals(
                _configuration["Actionstep:Environment"],
                "Production",
                StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;

            Changed(nameof(SelectedEnvironmentIndex));
            Changed(nameof(SelectedEnvironment));

            await ConnectEnvironmentAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task ConnectEnvironmentAsync()
    {
        if (_configuration == null)
            return;

        SetBusy(true);

        using var operation = new CancellationTokenSource();
        _operation = operation;

        try
        {
            _isAuthed = false;

            _client?.Dispose();
            _client = null;

            var cfg = new ConfigurationBuilder()
                .AddConfiguration(_configuration)
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Actionstep:Environment"] = SelectedEnvironment
                    })
                .Build();

            _client = new ActionstepClient(cfg);

            _rawBody = "";
            await RenderOutputAsync();

            _isAuthed = await _client.TryResumeAsync(operation.Token);

            StatusText = _isAuthed
                ? $"Logged in to {SelectedEnvironment}. Ready."
                : "Log in to run a request.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Session restore stopped.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _operation = null;
            SetBusy(false);
        }
    }

    private async void Auth_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsIdle)
            return;

        if (_client == null)
        {
            StatusText = "Check appsettings.json and restart the app.";
            return;
        }

        if (_isAuthed)
        {
            SetBusy(true);

            try
            {
                _client.Logout();
                _isAuthed = false;
                _rawBody = "";

                await RenderOutputAsync();

                StatusText = "Logged out.";
            }
            finally
            {
                SetBusy(false);
            }

            return;
        }

        SetBusy(true);

        using var operation =
            new CancellationTokenSource(TimeSpan.FromMinutes(5));

        _operation = operation;
        StatusText = "Complete sign-in in your browser.";

        try
        {
            _isAuthed = await _client.LoginInteractiveAsync(
                operation.Token);

            StatusText = _isAuthed
                ? $"Logged in to {SelectedEnvironment}. Ready."
                : "Sign-in did not complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sign-in timed out or was canceled.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _operation = null;
            SetBusy(false);
        }
    }

    private async void Run_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!CanRun || _client == null)
            return;

        SetBusy(true);

        using var operation = new CancellationTokenSource();
        _operation = operation;

        var pages = new List<ActionstepClient.GetResponse>();
        var allPages = FetchAllPages;

        _rawBody = "";
        await RenderOutputAsync();

        try
        {
            var uri = _client.ResolveRequestUri(
                Endpoint.Replace("\r", "").Replace("\n", ""));

            if (RequestMethod == "POST")
            {
                using var document = JsonDocument.Parse(RequestBody);
                StatusText = "Posting data...";
                var response = await _client.PostAsync(
                    uri.AbsoluteUri,
                    RequestBody,
                    operation.Token);
                pages.Add(response);

                StatusText =
                    $"HTTP {response.StatusCode} {response.Reason}. " +
                    (response.IsSuccess ? "POST completed." : "POST failed.");
            }
            else
            {
                var resource = uri.AbsolutePath["/api/rest/".Length..]
                    .Split('/')[0];

                var seen = new HashSet<string>(StringComparer.Ordinal);

                while (true)
                {
                    operation.Token.ThrowIfCancellationRequested();

                    if (!seen.Add(uri.AbsoluteUri))
                    {
                        throw new InvalidOperationException(
                            "Pagination repeated a request; stopped.");
                    }

                    StatusText = $"Fetching response {pages.Count + 1}...";

                    var response = await _client.GetAsync(
                        uri.AbsoluteUri,
                        operation.Token);

                    pages.Add(response);

                    if (!response.IsSuccess)
                    {
                        StatusText =
                            $"HTTP {response.StatusCode} {response.Reason}. " +
                            $"Stopped; {pages.Count} response(s) retained.";

                        break;
                    }

                    StatusText =
                        $"HTTP {response.StatusCode} {response.Reason} · " +
                        $"{pages.Count} page(s) fetched.";

                    if (!allPages)
                        break;

                    if (!ResponseDisplay.TryReadNextPage(
                        response.Body,
                        resource,
                        out var next))
                    {
                        StatusText +=
                            " No paging metadata for this endpoint; stopped.";

                        break;
                    }

                    if (next == null)
                        break;

                    uri = ResponseDisplay.WithPage(uri, next.Value);
                }
            }
        }
        catch (OperationCanceledException)
            when (operation.IsCancellationRequested)
        {
            StatusText =
                $"Stopped. Partial result: " +
                $"{pages.Count} completed response(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText =
                $"Request timed out. Partial result: " +
                $"{pages.Count} completed response(s).";
        }
        catch (Exception ex)
        {
            StatusText =
                $"{ex.Message} Retained " +
                $"{pages.Count} completed response(s).";
        }
        finally
        {
            if (pages.Count > 1)
                StatusText += " Responses are grouped in pages[].";

            _rawBody = ResponseDisplay.Combine(pages);
            await RenderOutputAsync();

            _operation = null;
            SetBusy(false);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFormatIndex == 2)
            CopyTable_Click(sender, e);
        else
            CopyText(Output);
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            StatusText = "Could not copy: " + ex.Message;
        }
    }

    private async Task RenderOutputAsync()
    {
        var version = ++_renderVersion;
        var body = _rawBody;

        var result = await Task.Run(() =>
        {
            var json = body;
            var yaml = body;
            var jsonNotice = "";
            var yamlNotice = "";
            var tables = new List<ResponseTable>();

            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var document = JsonDocument.Parse(body);

                    json = JsonSerializer.Serialize(
                        document.RootElement,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                    tables = BuildTables(document.RootElement);
                }
                catch (JsonException)
                {
                    jsonNotice =
                        "Not a JSON response. Showing the original response.";
                }

                try
                {
                    yaml = ResponseDisplay.ToYaml(body);
                }
                catch (Exception)
                {
                    yamlNotice =
                        "Could not convert to YAML. Showing the original response.";
                }
            }

            return (json, yaml, jsonNotice, yamlNotice, tables);
        });

        if (version != _renderVersion)
            return;

        var previousTable = SelectedTable?.Name;
        _rendering = true;

        try
        {
            _jsonOutput = result.json;
            _yamlOutput = result.yaml;
            _jsonNotice = result.jsonNotice;
            _yamlNotice = result.yamlNotice;
            _tables = result.tables;

            Changed(nameof(JsonOutput));
            Changed(nameof(YamlOutput));
            Changed(nameof(Tables));

            SelectedTable =
                _tables.FirstOrDefault(t => t.Name == previousTable)
                ?? _tables.FirstOrDefault();

            Changed(nameof(HasOutput));
            Changed(nameof(DisplayNotice));
        }
        finally
        {
            _rendering = false;
        }

        for (var view = 0; view < 3; view++)
            RebuildFind(view);
    }

    private static List<ResponseTable> BuildTables(JsonElement root)
    {
        var tables = new List<ResponseTable>();

        void Add(string name, JsonElement value)
        {
            if (value.ValueKind is not
                (JsonValueKind.Array or JsonValueKind.Object))
            {
                return;
            }

            var table = tables.FirstOrDefault(t => t.Name == name);

            if (table == null)
            {
                table = new ResponseTable(name);
                tables.Add(table);
            }

            void AddRow(JsonElement item)
            {
                var values = new Dictionary<string, string>(
                    StringComparer.Ordinal);

                void Flatten(JsonElement node, string path)
                {
                    if (node.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in node.EnumerateObject())
                        {
                            Flatten(
                                property.Value,
                                path.Length == 0
                                    ? property.Name
                                    : path + "." + property.Name);
                        }
                    }
                    else
                    {
                        values[path.Length == 0 ? "value" : path] =
                            node.ValueKind switch
                            {
                                JsonValueKind.String =>
                                    node.GetString() ?? "",
                                JsonValueKind.Null => "",
                                _ => node.GetRawText()
                            };
                    }
                }

                Flatten(item, "");

                foreach (var key in values.Keys)
                {
                    if (!table.Data.Columns
                        .Cast<DataColumn>()
                        .Any(c => c.Caption == key))
                    {
                        var column = table.Data.Columns.Add(
                            "c" + table.Data.Columns.Count,
                            typeof(string));

                        column.Caption = key;
                    }
                }

                var row = table.Data.NewRow();

                foreach (DataColumn column in table.Data.Columns)
                {
                    row[column] = values.GetValueOrDefault(
                        column.Caption,
                        "");
                }

                table.Data.Rows.Add(row);
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    AddRow(item);
            }
            else
            {
                AddRow(value);
            }
        }

        void Collect(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                Add("response", node);
                return;
            }

            if (node.ValueKind != JsonValueKind.Object)
                return;

            // The tool's multi-page wrapper retains each API response.
            if (node.TryGetProperty("pages", out var pages) &&
                pages.ValueKind == JsonValueKind.Array &&
                node.EnumerateObject().Count() == 1)
            {
                foreach (var page in pages.EnumerateArray())
                    Collect(page);

                return;
            }

            foreach (var property in node.EnumerateObject())
            {
                if (property.Name is "links" or "meta")
                    continue;

                if (property.Name == "linked" &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var linked in property.Value.EnumerateObject())
                        Add("linked." + linked.Name, linked.Value);
                }
                else
                {
                    Add(property.Name, property.Value);
                }
            }
        }

        Collect(root);
        return tables;
    }

    private void TableGrid_AutoGeneratingColumn(
        object sender,
        DataGridAutoGeneratingColumnEventArgs e)
    {
        var column = SelectedTable?.Data.Columns[e.PropertyName];

        if (column == null)
            return;

        e.Column.Header = column.Caption;
        e.Column.SortMemberPath = e.PropertyName;

        if (e.Column is DataGridTextColumn textColumn)
        {
            textColumn.Binding =
                new Binding("[" + e.PropertyName + "]");
        }
    }

    private void TableGrid_Sorting(
        object sender,
        DataGridSortingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => RebuildFind(2)));
    }

    private void TableGrid_RightClick(
        object sender,
        MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;

        while (node != null && node is not DataGridCell)
        {
            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        if (node is DataGridCell cell &&
            cell.DataContext is DataRowView)
        {
            TableGrid.CurrentCell = new DataGridCellInfo(cell);
            TableGrid.SelectedCells.Clear();
            TableGrid.SelectedCells.Add(TableGrid.CurrentCell);
        }
        else
        {
            TableGrid.CurrentCell = default;
        }
    }

    private void TableGrid_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        if (TableGrid.CurrentCell.Item is not DataRowView)
            e.Handled = true;
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (TableGrid.CurrentCell.Item is DataRowView row &&
            TableGrid.CurrentColumn is { } column)
        {
            CopyText(Convert.ToString(
                row[column.SortMemberPath],
                CultureInfo.InvariantCulture) ?? "");
        }
    }

    private void CopyColumn_Click(object sender, RoutedEventArgs e)
    {
        if (TableGrid.CurrentColumn is not { } column)
            return;

        CopyText(string.Join(
            Environment.NewLine,
            TableGrid.Items
                .OfType<DataRowView>()
                .Select(row => TsvCell(row[column.SortMemberPath]))));
    }

    private void CopyTable_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTable == null)
            return;

        var columns = TableGrid.Columns
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        var lines = new List<string>
        {
            string.Join("\t", columns.Select(c => TsvCell(c.Header)))
        };

        foreach (var row in TableGrid.Items.OfType<DataRowView>())
        {
            lines.Add(string.Join(
                "\t",
                columns.Select(c => TsvCell(row[c.SortMemberPath]))));
        }

        CopyText(string.Join(Environment.NewLine, lines));
    }

    private static string TsvCell(object? value)
    {
        var text = Convert.ToString(
            value,
            CultureInfo.InvariantCulture) ?? "";

        return text.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private TextBox ResponseBox(int view)
    {
        return view == 1
            ? YamlResponseTextBox
            : JsonResponseTextBox;
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        SearchResponse(SelectedFormatIndex, true);
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        var state = _findStates[SelectedFormatIndex];

        if (state.Matches.Count > 0)
            ShowFindMatch((state.Index + 1) % state.Matches.Count);
    }

    private void FindTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_restoringFind ||
            FindCountText == null ||
            FindNextButton == null)
        {
            return;
        }

        var state = _findStates[SelectedFormatIndex];
        state.Input = FindTextBox.Text;
        state.Query = "";

        ResetFind(SelectedFormatIndex);
        RefreshFindCounter();
    }

    private void ResponseTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_rendering ||
            FindCountText == null ||
            FindNextButton == null)
        {
            return;
        }

        RebuildFind(
            ReferenceEquals(sender, YamlResponseTextBox) ? 1 : 0);
    }

    private void RebuildFind(int view)
    {
        if (!string.IsNullOrEmpty(_findStates[view].Query))
            SearchResponse(view, false);
        else
            ResetFind(view);

        if (view == SelectedFormatIndex)
            RefreshFindCounter();
    }

    private void SearchResponse(int view, bool focus)
    {
        var state = _findStates[view];
        state.Query = state.Input;

        ResetFind(view);

        if (!string.IsNullOrEmpty(state.Query))
        {
            if (view == 2)
            {
                foreach (var row in TableGrid.Items.OfType<DataRowView>())
                {
                    foreach (var column in TableGrid.Columns
                        .OrderBy(c => c.DisplayIndex))
                    {
                        var text = Convert.ToString(
                            row[column.SortMemberPath],
                            CultureInfo.InvariantCulture) ?? "";

                        if (text.Contains(
                            state.Query,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            state.Matches.Add(new FindMatch(
                                0,
                                row,
                                column.SortMemberPath));
                        }
                    }
                }
            }
            else
            {
                var text = ResponseBox(view).Text;
                var start = 0;

                while (start <= text.Length - state.Query.Length)
                {
                    var match = text.IndexOf(
                        state.Query,
                        start,
                        StringComparison.OrdinalIgnoreCase);

                    if (match < 0)
                        break;

                    state.Matches.Add(new FindMatch(match));
                    start = match + state.Query.Length;
                }
            }

            if (state.Matches.Count > 0)
            {
                state.Index = 0;

                if (view == SelectedFormatIndex)
                    ShowFindMatch(0, focus);
            }
        }

        if (view == SelectedFormatIndex)
            RefreshFindCounter();
    }

    private void ResetFind(int view)
    {
        var state = _findStates[view];
        state.Matches.Clear();
        state.Index = -1;

        if (view == 2)
            TableGrid.SelectedCells.Clear();
        else
            ResponseBox(view).Select(0, 0);
    }

    private void RestoreFindControls()
    {
        if (FindTextBox == null ||
            FindCountText == null ||
            FindNextButton == null)
        {
            return;
        }

        _restoringFind = true;

        try
        {
            FindTextBox.Text =
                _findStates[SelectedFormatIndex].Input;
        }
        finally
        {
            _restoringFind = false;
        }

        RefreshFindCounter();
    }

    private void RefreshFindCounter()
    {
        var state = _findStates[SelectedFormatIndex];

        FindNextButton.IsEnabled = state.Matches.Count > 0;

        FindCountText.Text = string.IsNullOrEmpty(state.Query)
            ? ""
            : $"{(state.Index >= 0 ? state.Index + 1 : 0)} / " +
              $"{state.Matches.Count}";
    }

    private void ShowFindMatch(int index, bool focus = true)
    {
        var view = SelectedFormatIndex;
        var state = _findStates[view];

        state.Index = index;
        var match = state.Matches[index];

        if (view == 2)
        {
            var column = TableGrid.Columns.FirstOrDefault(
                c => c.SortMemberPath == match.Column);

            if (column != null && match.Row != null)
            {
                TableGrid.ScrollIntoView(match.Row, column);
                TableGrid.CurrentCell =
                    new DataGridCellInfo(match.Row, column);

                TableGrid.SelectedCells.Clear();
                TableGrid.SelectedCells.Add(TableGrid.CurrentCell);

                if (focus)
                    TableGrid.Focus();
            }
        }
        else
        {
            var box = ResponseBox(view);

            if (focus)
                box.Focus();

            box.Select(match.Start, state.Query.Length);

            var line = box.GetLineIndexFromCharacterIndex(
                match.Start);

            if (line >= 0)
                box.ScrollToLine(line);

            box.UpdateLayout();

            var bounds = box.GetRectFromCharacterIndex(
                match.Start);

            if (!bounds.IsEmpty)
            {
                box.ScrollToHorizontalOffset(Math.Max(
                    0,
                    box.HorizontalOffset +
                    bounds.Left -
                    box.ViewportWidth / 3));
            }
        }

        RefreshFindCounter();
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        RefreshState();
    }

    private void RefreshState()
    {
        Changed(nameof(IsAuthed));
        Changed(nameof(IsIdle));
        Changed(nameof(CanRun));
        Changed(nameof(CanFetchAllPages));
        Changed(nameof(AuthButtonText));
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(name));
    }
}