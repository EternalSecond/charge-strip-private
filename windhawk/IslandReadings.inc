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

static std::vector<std::wstring> ChargeStripFields(std::wstring const& text, wchar_t separator) {
    std::vector<std::wstring> result;
    for (size_t start = 0;;) {
        auto end = text.find(separator, start);
        auto field = text.substr(start, end - start);
        if (!field.empty() && field.back() == L'\r') field.pop_back();
        result.push_back(std::move(field));
        if (end == std::wstring::npos) return result;
        start = end + 1;
    }
}

static TextBlock ChargeStripText(std::wstring const& text, double size, uint32_t rgb) {
    TextBlock label;
    label.Text(text);
    label.FontSize(size);
    label.Foreground(Brush(Color(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255)));
    label.TextTrimming(xaml::TextTrimming::CharacterEllipsis);
    return label;
}

static void RequestChargeStripPanel(bool battery) {
    wchar_t local[32768];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, ARRAYSIZE(local));
    if (!length || length >= ARRAYSIZE(local)) return;
    POINT cursor{};
    if (!GetCursorPos(&cursor)) return;
    auto request = std::to_string(battery ? 1 : 2) + "," + std::to_string(cursor.x) + "," + std::to_string(cursor.y);
    std::wstring path(local, length);
    path += L"\\Charge Strip\\island-click.txt";
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, request.data(), static_cast<DWORD>(request.size()), &written, nullptr);
    CloseHandle(file);
}

static StackPanel MakeChargeStripReadings() {
    StackPanel strip;
    strip.Name(L"ChargeStrip_InlineReadings");
    strip.Orientation(controls::Orientation::Horizontal);
    strip.VerticalAlignment(VerticalAlignment::Center);
    auto makeButton = [](bool battery) {
        controls::Button button;
        button.Height(25);
        button.Width(battery ? 44 : 57);
        button.Margin({2, 0, 2, 0});
        button.Padding({4, 0, 4, 0});
        button.Background(Brush(Color(14, 237, 235, 239)));
        button.BorderThickness({0, 0, 0, 0});
        button.CornerRadius({9, 9, 9, 9});
        button.Click([battery](auto const&, auto const&) {
            try { RequestChargeStripPanel(battery); } catch (...) {}
        });
        return button;
    };
    auto batteries = makeButton(true);
    auto resources = makeButton(false);
    strip.Children().Append(batteries);
    strip.Children().Append(resources);
    winrt::Windows::UI::Xaml::DispatcherTimer timer;
    timer.Interval(std::chrono::milliseconds(1200));
    timer.Tick([a = winrt::make_weak(batteries), b = winrt::make_weak(resources), last = std::wstring{}](auto const&, auto const&) mutable {
        try {
            auto battery = a.get();
            auto resource = b.get();
            if (!battery || !resource) return;
            auto data = ChargeStripReadings();
            if (data == last) return;
            last = data;
            auto rows = ChargeStripFields(data, L'\n');
            auto values = ChargeStripFields(rows[0], L',');
            StackPanel icons;
            icons.Orientation(controls::Orientation::Horizontal);
            for (auto const& value : values) {
                if (value.empty()) continue;
                int level = std::clamp(static_cast<int>(wcstol(value.c_str(), nullptr, 10)), -1, 100);
                auto ink = Brush(Color(255, 237, 235, 239));
                if (icons.Children().Size() == 0) {
                    Grid headphones;
                    headphones.Width(14);
                    headphones.Height(13);
                    headphones.Margin({1, 0, 4, 0});
                    Border band;
                    band.Width(11);
                    band.Height(9);
                    band.VerticalAlignment(VerticalAlignment::Top);
                    band.CornerRadius({5, 5, 0, 0});
                    band.BorderBrush(ink);
                    band.BorderThickness({1.3, 1.3, 1.3, 0});
                    headphones.Children().Append(band);
                    for (auto side : {HorizontalAlignment::Left, HorizontalAlignment::Right}) {
                        Border ear;
                        ear.Width(3);
                        ear.Height(5);
                        ear.Margin({1, 6, 1, 0});
                        ear.VerticalAlignment(VerticalAlignment::Top);
                        ear.HorizontalAlignment(side);
                        ear.CornerRadius({1, 1, 1, 1});
                        ear.Background(ink);
                        headphones.Children().Append(ear);
                    }
                    Border levelBar;
                    levelBar.Width(10.0 * std::max(0, level) / 100);
                    levelBar.Height(1);
                    levelBar.VerticalAlignment(VerticalAlignment::Bottom);
                    levelBar.Background(ink);
                    headphones.Children().Append(levelBar);
                    icons.Children().Append(headphones);
                    continue;
                }
                Border shell;
                shell.Width(13);
                shell.Height(9);
                shell.Margin({1, 0, 2, 0});
                shell.VerticalAlignment(VerticalAlignment::Center);
                shell.CornerRadius({2, 2, 2, 2});
                shell.BorderBrush(ink);
                shell.BorderThickness({1, 1, 1, 1});
                Border fill;
                fill.Width(9.0 * std::max(0, level) / 100);
                fill.Height(4);
                fill.HorizontalAlignment(HorizontalAlignment::Left);
                fill.VerticalAlignment(VerticalAlignment::Center);
                fill.Margin({1, 0, 0, 0});
                fill.Background(ink);
                shell.Child(fill);
                icons.Children().Append(shell);
            }
            battery.Content(icons);
            battery.Visibility(icons.Children().Size() ? Visibility::Visible : Visibility::Collapsed);
            StackPanel summary;
            bool hasResources = false;
            for (size_t i = 3; i < rows.size(); ++i) {
                auto fields = ChargeStripFields(rows[i], L'\t');
                if (fields.size() == 1 && fields[0] == L"R") hasResources = true;
                if (fields.size() == 2 && summary.Children().Size() < 2)
                    summary.Children().Append(ChargeStripText(fields[0] + L" " + fields[1], 9, 0xEDEBEF));
            }
            if (summary.Children().Size() == 0 && hasResources)
                summary.Children().Append(ChargeStripText(L"PC", 11, 0xEDEBEF));
            resource.Content(summary);
            resource.Visibility(summary.Children().Size() ? Visibility::Visible : Visibility::Collapsed);
        } catch (...) {}
    });
    strip.Loaded([timer](auto const&, auto const&) { timer.Start(); });
    strip.Unloaded([timer](auto const&, auto const&) { timer.Stop(); });
    timer.Start();
    return strip;
}
