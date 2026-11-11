using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace ExcellApp
{
    public partial class MainPage : ContentPage
    {
        const int InitialRows = 50;
        const int InitialColumns = 20;   // стовпці A..T

        const double HeaderWidth = 44;
        const double HeaderHeight = 32;
        const double CellWidth = 96;
        const double CellHeight = 40;

        const string ErrorText = "#ПОМИЛКА";

        static readonly FilePickerFileType JsonFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.WinUI, new[] { ".json" } },
            { DevicePlatform.Android, new[] { "application/json" } },
            { DevicePlatform.iOS, new[] { "public.json" } },
            { DevicePlatform.MacCatalyst, new[] { "public.json" } }
        });

        Spreadsheet _sheet = new(InitialRows, InitialColumns);

        // Елементи інтерфейсу: _entries[рядок][стовпець] (нумерація з 0)
        readonly List<List<Entry>> _entries = new();
        readonly List<Label> _columnLabels = new();
        readonly List<Label> _rowLabels = new();

        bool _showValues = true;          // true — режим ЗНАЧЕННЯ, false — режим ВИРАЗ
        Entry? _focusedEntry;             // клітинка, яка зараз редагується
        int _selectedRow = -1;            // остання вибрана клітинка
        int _selectedColumn = -1;
        string _lastFileName = "table";

        public MainPage()
        {
            InitializeComponent();
            BuildGrid();
            RefreshAll();
            UpdateStatus();
        }

        // =====================================================================
        //  Побудова та зміна візуальної таблиці
        // =====================================================================

        private void BuildGrid()
        {
            foreach (var rowEntries in _entries)
                foreach (var entry in rowEntries)
                    DetachEntry(entry);

            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();
            _entries.Clear();
            _columnLabels.Clear();
            _rowLabels.Clear();
            _focusedEntry = null;
            _selectedRow = -1;
            _selectedColumn = -1;
            textInput.Text = string.Empty;

            // нульовий стовпець — номери рядків, нульовий рядок — назви стовпців
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(HeaderWidth)));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(HeaderHeight)));

            for (int c = 0; c < _sheet.Columns; c++) AddColumnUi();
            for (int r = 0; r < _sheet.Rows; r++) AddRowUi();
        }

        private void AddColumnUi()
        {
            int column = _columnLabels.Count;
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(CellWidth)));

            var label = CreateHeaderLabel(CellAddress.GetColumnName(column + 1));
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, column + 1);
            grid.Children.Add(label);
            _columnLabels.Add(label);

            for (int row = 0; row < _entries.Count; row++)
            {
                var entry = CreateEntry();
                Grid.SetRow(entry, row + 1);
                Grid.SetColumn(entry, column + 1);
                grid.Children.Add(entry);
                _entries[row].Add(entry);
            }
        }

        private void AddRowUi()
        {
            int row = _rowLabels.Count;
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(CellHeight)));

            var label = CreateHeaderLabel((row + 1).ToString());
            Grid.SetRow(label, row + 1);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
            _rowLabels.Add(label);

            var rowEntries = new List<Entry>();
            for (int column = 0; column < _columnLabels.Count; column++)
            {
                var entry = CreateEntry();
                Grid.SetRow(entry, row + 1);
                Grid.SetColumn(entry, column + 1);
                grid.Children.Add(entry);
                rowEntries.Add(entry);
            }
            _entries.Add(rowEntries);
        }

        private void RemoveLastRowUi()
        {
            int row = _entries.Count - 1;

            foreach (var entry in _entries[row])
            {
                DetachEntry(entry);
                grid.Children.Remove(entry);
            }

            grid.Children.Remove(_rowLabels[row]);
            _entries.RemoveAt(row);
            _rowLabels.RemoveAt(row);
            grid.RowDefinitions.RemoveAt(row + 1);
        }

        private void RemoveLastColumnUi()
        {
            int column = _columnLabels.Count - 1;

            foreach (var rowEntries in _entries)
            {
                var entry = rowEntries[column];
                DetachEntry(entry);
                grid.Children.Remove(entry);
                rowEntries.RemoveAt(column);
            }

            grid.Children.Remove(_columnLabels[column]);
            _columnLabels.RemoveAt(column);
            grid.ColumnDefinitions.RemoveAt(column + 1);
        }

        private static Label CreateHeaderLabel(string text) => new()
        {
            Text = text,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };

        private Entry CreateEntry()
        {
            var entry = new Entry
            {
                Text = string.Empty,
                MaxLength = 2000,
                FontSize = 13,
                Margin = new Thickness(1),
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill
            };
            entry.Focused += Entry_Focused;
            entry.Unfocused += Entry_Unfocused;
            entry.TextChanged += Entry_TextChanged;
            entry.Completed += Entry_Completed;
            return entry;
        }

        private void DetachEntry(Entry entry)
        {
            entry.Focused -= Entry_Focused;
            entry.Unfocused -= Entry_Unfocused;
            entry.TextChanged -= Entry_TextChanged;
            entry.Completed -= Entry_Completed;
            if (ReferenceEquals(_focusedEntry, entry)) _focusedEntry = null;
        }

        // =====================================================================
        //  Відображення: режими ВИРАЗ / ЗНАЧЕННЯ
        // =====================================================================

        private void RecalculateAndRefresh()
        {
            _sheet.Recalculate();
            RefreshAll();
            UpdateStatus();
        }

        /// Оновлює текст усіх клітинок, окрім тієї, що зараз редагується.
        private void RefreshAll()
        {
            for (int row = 0; row < _entries.Count; row++)
            {
                for (int column = 0; column < _entries[row].Count; column++)
                {
                    var entry = _entries[row][column];
                    if (ReferenceEquals(entry, _focusedEntry)) continue;
                    ApplyDisplay(entry, row, column);
                }
            }
        }

        private void ApplyDisplay(Entry entry, int row, int column)
        {
            CellResult result = _sheet.GetResult(row, column);

            string text;
            if (!_showValues)
            {
                text = _sheet.GetExpression(row, column);          // режим ВИРАЗ
            }
            else
            {
                text = result.Kind switch                          // режим ЗНАЧЕННЯ
                {
                    CellResultKind.Value => result.Value.ToString(),
                    CellResultKind.Error => ErrorText,
                    _ => string.Empty
                };
            }

            if (entry.Text != text) entry.Text = text;

            if (result.Kind == CellResultKind.Error)
                entry.TextColor = Colors.Red;
            else
                entry.ClearValue(Entry.TextColorProperty);
        }

        private void UpdateStatus()
        {
            if (_selectedRow < 0 || _selectedRow >= _sheet.Rows ||
                _selectedColumn < 0 || _selectedColumn >= _sheet.Columns)
            {
                selectedCellLabel.Text = string.Empty;
                statusLabel.Text = "Виберіть клітинку, щоб побачити її значення або опис помилки.";
                statusLabel.ClearValue(Label.TextColorProperty);
                return;
            }

            var address = new CellAddress(_selectedRow, _selectedColumn);
            selectedCellLabel.Text = address.ToString();

            CellResult result = _sheet.GetResult(_selectedRow, _selectedColumn);
            switch (result.Kind)
            {
                case CellResultKind.Error:
                    statusLabel.Text = $"{address}: ПОМИЛКА — {result.Error}";
                    statusLabel.TextColor = Colors.Red;
                    break;
                case CellResultKind.Value:
                    statusLabel.Text = $"{address} = {result.Value}";
                    statusLabel.ClearValue(Label.TextColorProperty);
                    break;
                default:
                    statusLabel.Text = $"{address}: порожня клітинка (у виразах вважається FALSE)";
                    statusLabel.ClearValue(Label.TextColorProperty);
                    break;
            }
        }

        private void NormalizeSelection()
        {
            if (_selectedRow >= _sheet.Rows || _selectedColumn >= _sheet.Columns)
            {
                _selectedRow = -1;
                _selectedColumn = -1;
                textInput.Text = string.Empty;
            }
        }

        // =====================================================================
        //  Події клітинок
        // =====================================================================

        private static (int Row, int Column) GetPosition(Entry entry) =>
            (Grid.GetRow(entry) - 1, Grid.GetColumn(entry) - 1);

        // Клітинка отримала фокус — показуємо її ВИРАЗ для редагування
        private void Entry_Focused(object? sender, FocusEventArgs e)
        {
            if (sender is not Entry entry) return;

            var (row, column) = GetPosition(entry);
            if (row < 0 || row >= _sheet.Rows || column < 0 || column >= _sheet.Columns) return;

            _focusedEntry = entry;
            _selectedRow = row;
            _selectedColumn = column;

            string expression = _sheet.GetExpression(row, column);
            if (entry.Text != expression) entry.Text = expression;
            textInput.Text = expression;
            entry.ClearValue(Entry.TextColorProperty);

            UpdateStatus();
        }

        // Користувач вийшов зі зміненої клітинки (втратив фокус) — застосовуємо вираз
        private void Entry_Unfocused(object? sender, FocusEventArgs e)
        {
            if (sender is not Entry entry) return;

            var (row, column) = GetPosition(entry);
            if (ReferenceEquals(_focusedEntry, entry)) _focusedEntry = null;
            if (row < 0 || row >= _sheet.Rows || column < 0 || column >= _sheet.Columns) return;

            _sheet.SetExpression(row, column, entry.Text);
            RecalculateAndRefresh();
        }

        // Дзеркалимо введений у клітинці текст у рядок формул
        private void Entry_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _focusedEntry))
                textInput.Text = e.NewTextValue;
        }

        // Enter у клітинці — завершити редагування
        private void Entry_Completed(object? sender, EventArgs e)
        {
            if (sender is Entry entry) entry.Unfocus();
        }

        // Enter у рядку формул — записати вираз у вибрану клітинку
        private void FormulaBar_Completed(object? sender, EventArgs e)
        {
            if (_selectedRow < 0 || _selectedRow >= _sheet.Rows ||
                _selectedColumn < 0 || _selectedColumn >= _sheet.Columns)
                return;

            _sheet.SetExpression(_selectedRow, _selectedColumn, textInput.Text);
            RecalculateAndRefresh();
        }

        // =====================================================================
        //  Кнопки (кожен обробник лише викликає логіку з MyExcelMAUIApp.Logic)
        // =====================================================================

        private void ModeButton_Clicked(object? sender, EventArgs e)
        {
            _showValues = !_showValues;
            ModeButton.Text = _showValues ? "Режим: ЗНАЧЕННЯ" : "Режим: ВИРАЗ";
            RefreshAll();
        }

        private async void CalculateButton_Clicked(object? sender, EventArgs e)
        {
            RecalculateAndRefresh();

            var errors = _sheet.GetErrors().ToList();
            if (errors.Count == 0)
            {
                await DisplayAlert("Обчислення",
                    $"Непорожніх клітинок: {_sheet.NonEmptyCellCount}. Помилок не знайдено.", "OK");
                return;
            }

            string list = string.Join("\n", errors.Take(10).Select(x => $"{x.Address}: {x.Message}"));
            string more = errors.Count > 10 ? $"\n… та ще помилок: {errors.Count - 10}" : string.Empty;
            await DisplayAlert("Обчислення", $"Знайдено помилок: {errors.Count}\n\n{list}{more}", "OK");
        }

        private async void SaveButton_Clicked(object? sender, EventArgs e)
        {
            try
            {
                string? name = await DisplayPromptAsync(
                    "Зберегти", "Введіть ім'я файлу (без розширення):",
                    "Зберегти", "Скасувати", initialValue: _lastFileName, maxLength: 60);

                if (string.IsNullOrWhiteSpace(name)) return;

                foreach (char invalid in Path.GetInvalidFileNameChars())
                    name = name.Replace(invalid, '_');
                name = name.Trim();

                string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string path = Path.Combine(folder, name + ".json");

                if (File.Exists(path))
                {
                    bool overwrite = await DisplayAlert("Файл існує",
                        $"Файл \"{Path.GetFileName(path)}\" вже існує. Перезаписати?", "Так", "Ні");
                    if (!overwrite) return;
                }

                await File.WriteAllTextAsync(path, SpreadsheetSerializer.Serialize(_sheet));
                _lastFileName = name;

                await DisplayAlert("Збережено", $"Таблицю збережено у файл:\n{path}", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Помилка", $"Не вдалося зберегти файл: {ex.Message}", "OK");
            }
        }

        private async void ReadButton_Clicked(object? sender, EventArgs e)
        {
            try
            {
                FileResult? file = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Виберіть файл таблиці (.json)",
                    FileTypes = JsonFileType
                });

                if (file == null) return;

                string json;
                using (var stream = await file.OpenReadAsync())
                using (var reader = new StreamReader(stream))
                {
                    json = await reader.ReadToEndAsync();
                }

                _sheet = SpreadsheetSerializer.Deserialize(json);
                _lastFileName = Path.GetFileNameWithoutExtension(file.FileName);

                BuildGrid();
                RecalculateAndRefresh();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Помилка", $"Не вдалося прочитати файл: {ex.Message}", "OK");
            }
        }

        private async void ExitButton_Clicked(object? sender, EventArgs e)
        {
            bool answer = await DisplayAlert("Підтвердження", "Ви дійсно хочете вийти?", "Так", "Ні");
            if (answer)
            {
                Application.Current?.Quit();
            }
        }

        private async void HelpButton_Clicked(object? sender, EventArgs e)
        {
            const string help =
                "Лабораторна робота 1. Електронна таблиця (аналіз та обчислення виразів).\n" +
                "Варіант 73: операції ^, =, <, >, not, or, and, eqv.\n" +
                "Автор: Зеленько Андрій, група К-25\n\n" +

                "КЕРУВАННЯ\n" +
                "• Клацніть на клітинку й введіть вираз. Результат обчислюється, коли ви залишаєте клітинку (або натискаєте Enter).\n" +
                "• Рядок над таблицею показує вираз вибраної клітинки; під ним — її значення або опис помилки.\n" +
                "• Кнопка «Режим» перемикає відображення: ЗНАЧЕННЯ (TRUE/FALSE) або ВИРАЗ (текст формули).\n" +
                "• «Порахувати» — перерахувати всю таблицю та показати знайдені помилки.\n" +
                "• «Зберегти» / «Прочитати» — запис і читання таблиці у файл .json (папка «Документи»).\n" +
                "• «Додати/Видалити рядок/стовпчик» — зміна розміру таблиці (видаляється останній).\n\n" +

                "ВИРАЗИ (логічні)\n" +
                "Операнди: цілі числа довільної довжини, імена клітинок (A1, B12), true, false, круглі дужки.\n" +
                "Порожня клітинка в посиланні вважається FALSE. Результат виразу має бути логічним.\n" +
                "Операції від найнижчого пріоритету до найвищого:\n" +
                "  1) eqv (еквівалентність)\n" +
                "  2) or\n" +
                "  3) and\n" +
                "  4) not\n" +
                "  5) =  <  >  (порівняння; = працює й для логічних значень)\n" +
                "  6) ^  (піднесення до степеня, права асоціативність)\n" +
                "Приклади: 2^10 > 1000    not (A1 and B1)    (A1 and true) eqv (2 < 3)\n\n" +

                "ПОМИЛКИ\n" +
                "Синтаксичні (неправильний вираз) та семантичні (невідповідність типів, циклічні посилання, " +
                "неіснуюча клітинка, результат не логічний) позначаються #ПОМИЛКА червоним кольором; " +
                "докладне пояснення — у рядку стану.";

            await DisplayAlert("Довідка", help, "OK");
        }

        private async void DeleteRowButton_Clicked(object? sender, EventArgs e)
        {
            if (_sheet.Rows <= 1)
            {
                await DisplayAlert("Видалення рядка", "У таблиці має залишитися хоча б один рядок.", "OK");
                return;
            }

            int last = _sheet.Rows - 1;
            if (!_sheet.IsRowEmpty(last))
            {
                bool confirm = await DisplayAlert("Видалення рядка",
                    $"Рядок {last + 1} містить дані. Видалити його?", "Так", "Ні");
                if (!confirm) return;
            }

            _sheet.RemoveLastRow();
            RemoveLastRowUi();
            NormalizeSelection();
            RecalculateAndRefresh();
        }

        private async void DeleteColumnButton_Clicked(object? sender, EventArgs e)
        {
            if (_sheet.Columns <= 1)
            {
                await DisplayAlert("Видалення стовпчика", "У таблиці має залишитися хоча б один стовпчик.", "OK");
                return;
            }

            int last = _sheet.Columns - 1;
            if (!_sheet.IsColumnEmpty(last))
            {
                bool confirm = await DisplayAlert("Видалення стовпчика",
                    $"Стовпчик {CellAddress.GetColumnName(last + 1)} містить дані. Видалити його?", "Так", "Ні");
                if (!confirm) return;
            }

            _sheet.RemoveLastColumn();
            RemoveLastColumnUi();
            NormalizeSelection();
            RecalculateAndRefresh();
        }

        private async void AddRowButton_Clicked(object? sender, EventArgs e)
        {
            if (_sheet.Rows >= Spreadsheet.MaxRows)
            {
                await DisplayAlert("Додавання рядка", $"Досягнуто максимум рядків: {Spreadsheet.MaxRows}.", "OK");
                return;
            }

            _sheet.AddRow();
            AddRowUi();
            RecalculateAndRefresh();
        }

        private async void AddColumnButton_Clicked(object? sender, EventArgs e)
        {
            if (_sheet.Columns >= Spreadsheet.MaxColumns)
            {
                await DisplayAlert("Додавання стовпчика", $"Досягнуто максимум стовпчиків: {Spreadsheet.MaxColumns}.", "OK");
                return;
            }

            _sheet.AddColumn();
            AddColumnUi();
            RecalculateAndRefresh();
        }
    }
}
