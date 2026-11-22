// Charge Strip additions for Island Media Controls by usho (MIT).
// The published mod's header, author and license remain in the generated source.
static std::wstring ChargeStripReadings() {
    wchar_t local[32768];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, ARRAYSIZE(local));
    if (!length || length >= ARRAYSIZE(local)) return {};
    std::wstring path(local, length);
    path += L"\\Charge Strip\\island-readings.txt";
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return {};
    FILETIME written{}, now{};
    GetSystemTimeAsFileTime(&now);
    char bytes[2048];
    DWORD read = 0;
    bool ok = GetFileTime(file, nullptr, nullptr, &written) &&
              ReadFile(file, bytes, sizeof(bytes), &read, nullptr) && read && read < sizeof(bytes);
    CloseHandle(file);
    auto ticks = [](FILETIME t) {
        return (static_cast<ULONGLONG>(t.dwHighDateTime) << 32) | t.dwLowDateTime;
    };
    if (!ok || (ticks(now) > ticks(written) && ticks(now) - ticks(written) > 350000000ULL)) return {};
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, read, nullptr, 0);
    if (size <= 0) return {};
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, read, result.data(), size);
    return result;
}

static void AddChargeStripReadings(Grid const& content) {
    ColumnDefinition column;
    column.Width({1, GridUnitType::Auto});
    content.ColumnDefinitions().Append(column);
    StackPanel inlineReadings;
    inlineReadings.Name(L"ChargeStrip_InlineReadings");
    inlineReadings.Orientation(controls::Orientation::Horizontal);
    inlineReadings.VerticalAlignment(VerticalAlignment::Center);
    inlineReadings.Margin({6, 0, 0, 0});
    Grid::SetColumn(inlineReadings, 3);
    content.Children().Append(inlineReadings);

    winrt::Windows::UI::Xaml::DispatcherTimer refresh;
    refresh.Interval(std::chrono::milliseconds(1500));
    auto redraw = [weak = winrt::make_weak(inlineReadings), last = std::wstring{}]() mutable {
        auto target = weak.get();
        if (!target) return;
        auto data = ChargeStripReadings();
        if (data == last) return;
        last = data;
        target.Children().Clear();
        controls::ToolTipService::SetToolTip(target, nullptr);
        if (data.empty()) return;
        std::vector<std::wstring> rows;
        for (size_t start = 0; start < data.size();) {
            size_t end = data.find(L'\n', start);
            if (end == std::wstring::npos) end = data.size();
            auto row = data.substr(start, end - start);
            if (!row.empty() && row.back() == L'\r') row.pop_back();
            rows.push_back(std::move(row));
            start = end + 1;
        }
        if (rows.size() < 3) return;
        auto tint = [](std::wstring const& hex, uint32_t fallback) {
            uint32_t rgb = hex.size() == 7 && hex[0] == L'#' ? wcstoul(hex.c_str() + 1, nullptr, 16) : fallback;
            return Brush(Color(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
        };
        auto accent = tint(rows[1], 0xC1BFC8);
        auto ink = tint(rows[2], 0xFFF0E8);
        std::wstring hint;
        size_t start = 0;
        for (int i = 0; i < 2 && start < rows[0].size(); ++i) {
            auto end = rows[0].find(L',', start);
            auto value = rows[0].substr(start, end - start);
            start = end == std::wstring::npos ? rows[0].size() : end + 1;
            int percent = std::clamp(static_cast<int>(wcstol(value.c_str(), nullptr, 10)), -1, 100);
            Grid cell;
            cell.Width(16);
            cell.Height(9);
            cell.Margin({2, 0, 1, 0});
            cell.VerticalAlignment(VerticalAlignment::Center);
            Border shell;
            shell.Width(13);
            shell.Height(9);
            shell.CornerRadius({2, 2, 2, 2});
            shell.HorizontalAlignment(HorizontalAlignment::Left);
            shell.BorderBrush(accent);
            shell.BorderThickness({1, 1, 1, 1});
            Border fill;
            fill.Width(9.0 * std::max(0, percent) / 100);
            fill.Height(5);
            fill.HorizontalAlignment(HorizontalAlignment::Left);
            fill.VerticalAlignment(VerticalAlignment::Center);
            fill.Margin({1, 0, 0, 0});
            fill.Background(accent);
            shell.Child(fill);
            cell.Children().Append(shell);
            Border terminal;
            terminal.Width(2);
            terminal.Height(4);
            terminal.HorizontalAlignment(HorizontalAlignment::Right);
            terminal.VerticalAlignment(VerticalAlignment::Center);
            terminal.Background(accent);
            cell.Children().Append(terminal);
            target.Children().Append(cell);
            if (!hint.empty()) hint += L"   ";
            hint += (percent < 0 ? std::wstring(L"—") : std::to_wstring(percent) + L"%");
        }
        if (rows.size() > 3) {
            StackPanel metrics;
            metrics.Margin({6, 0, 2, 0});
            metrics.VerticalAlignment(VerticalAlignment::Center);
            for (size_t i = 3; i < rows.size(); ++i) {
                auto divider = rows[i].find(L'\t');
                if (divider == std::wstring::npos) continue;
                std::wstring text = rows[i].substr(0, divider) + L"  " + rows[i].substr(divider + 1);
                if (!hint.empty()) hint += L"\n";
                hint += text;
                if (i > 4) continue;
                TextBlock metric;
                metric.FontSize(10);
                metric.Foreground(ink);
                metric.Text(text);
                metrics.Children().Append(metric);
            }
            target.Children().Append(metrics);
        }
        if (!hint.empty()) controls::ToolTipService::SetToolTip(target, winrt::box_value(winrt::hstring(hint)));
    };
    refresh.Tick([redraw](auto const&, auto const&) mutable { try { redraw(); } catch (...) {} });
    inlineReadings.Loaded([refresh](auto const&, auto const&) { refresh.Start(); });
    inlineReadings.Unloaded([refresh](auto const&, auto const&) { refresh.Stop(); });
}
