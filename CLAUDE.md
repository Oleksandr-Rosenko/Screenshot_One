# Screenshot One — пам'ятка для AI-асистентів (Claude Code, Antigravity тощо)

Програма для скріншотів і запису екрана для Windows і macOS. Avalonia 12 + SukiUI, .NET 10.
Інтерфейс — українською, англійською й російською (з 2.8.0); коментарі в коді, повідомлення комітів і нотатки до релізів пишемо **українською**.

## Локалізація (2.8.0)

- Усі рядки інтерфейсу — у `Strings.cs` (ключ → `{ uk, en, ru }`). **Жодного тексту інтерфейсу прямо в XAML чи коді.**
- XAML: `Text="{DynamicResource Settings_Theme}"` (відкриті вікна міняють мову одразу). Код: `Loc.T("Ключ")`, з аргументами — `Loc.F("Ключ", x)` і `{0}` у рядку.
- `Loc.Apply(AppSettings.Language)` — "auto" (мова ОС: uk → українська, ru → російська, інше → англійська; на Mac — `defaults read -g AppleLanguages`) або "uk"/"en"/"ru". Меню трею перебудовується за подією `Loc.LanguageChanged`.
- Новий рядок — у всіх трьох мовах з однаковими `{N}`; `LocTests` перевіряє повноту й те, що кожен ключ з XAML/коду існує.
- Журнал (`AppLog`) і технічні винятки — українською, не перекладаються.
- Нотатки до релізу — у трьох файлах: `ReleaseNotes.md` (uk), `ReleaseNotes.en.md`, `ReleaseNotes.ru.md` з однаковою кількістю пунктів (`LocTests` перевіряє). `publish_notes.py` складає тіло релізу з розділами `<!-- lang:xx -->…<!-- /lang -->` (en/ru — у `<details>`), вікно оновлення показує розділ мовою програми (`UpdateWindow.ExtractNotes`, запасна — українська).

## Структура

Розв'язок `ScreenShot_One_Avalonia.slnx`: два проєкти.

- `ScreenShot_One_Avalonia/` — програма (`AssemblyName` = `Screenshot_One`). Дві цілі: `net10.0-windows10.0.19041.0` (Windows SDK — потрібен для `Windows.Media.Ocr`; запуск з Windows 10 1809) і `net10.0` (macOS). В умовах `.csproj` — `$(TargetFramework.StartsWith('net10.0-windows'))`.
  - `Program.cs` — точка входу, Velopack-хук, глобальні обробники помилок.
  - `App.axaml.cs` — трей-меню, гарячі клавіші, `ShowSingle<T>()` (показ вікна в одному екземплярі), фонова перевірка оновлень.
  - `MainWindow` — оверлей виділення зони для скріншота.
  - `EditorWindow` — редактор скріншотів (~1900 рядків code-behind): фігури (`ShapeType`), undo/redo, обрізка, вставка, копіювання, завантаження.
  - `HistoryWindow` + `HistoryManager` — історія. Файли `{id}_original.png`, `{id}_flattened.png`, `{id}_meta.json`; ліміт 500; ID `yyyyMMdd_HHmmss_fff`.
    Центрування галереї — відступами панелі від реального місця, яке дає шаблон списку (`GallerySpace`), а не від фіксованої ширини.
    Вікно історії показує сторінки по 40 (`PageSize`, `AppendPage` при прокрутці), мініатюри декодуються паралельно (`Bitmap.DecodeToWidth`).
    Редактор зберігає через `HistoryManager.ReserveId` + `SaveScreenshotInBackground` (фонова черга; `_flattened.png` пишеться останнім і атомарно). Перед виходом/оновленням — `HistoryManager.WaitForPendingSaves`.
  - `VideoControlWindow`, `VideoRegionWindow`, `VideoRecorderManager` — запис відео. На Windows ScreenRecorderLib, на macOS ffmpeg. Інтерфейс `IVideoRecorderService`.
  - `VideoEditorWindow` — відеоплеєр (LibVLCSharp) і обрізка (ffmpeg).
  - `ScreenCaptureService` (`IScreenCaptureService`: Windows/Mac/Linux), `ScreenService`, `PlatformHelper` — платформна логіка.
    Windows: GDI `CopyFromScreen` + пряме копіювання пікселів (`LockBits`). Mac (з 2.8.3), по черзі: macOS 14+ — ScreenCaptureKit через Swift-утиліту `MacHelpers/capture.swift` → `Contents/MacOS/capture-helper` (`MacScreenCaptureKit`); далі `MacCoreGraphics` (CoreGraphics `CGDisplayCreateImage`, застарілий з macOS 14); далі `/usr/sbin/screencapture`.
    `capture-helper`: `--display N` / `--all` / `--rect X,Y,W,H` (ТОЧКИ, глобальні координати) / `--list` / `--selftest`; вихід `--out файл.png` або `-`, а програма бере `--raw` — сирі BGRA у stdout (заголовок `SOC1`, `MacScreenCaptureKit.ParseRaw`), без PNG. Прямокутник Avalonia (пікселі) ділиться на `Screen.Scaling`, `--scale` дає рівно стільки ж пікселів — розмір і DPI (96 × масштаб) як у CoreGraphics. Коди виходу: 1 — аргументи, 2 — знімок, 3 — запис, 4 — немає дозволу «Запис екрана», 5 — тайм-аут. Після 2 збоїв поспіль (крім коду 4) утиліта в сесії більше не запускається. Збирається з ціллю macOS 14 (сама програма — 10.15+); CI компілює під arm64 і x64 і запускає `--selftest` (код 4 на раннері — пропуск).
  - `BitmapPixels` — обрізка / копіювання / зміна DPI бітмапів у пам'яті (без PNG). DPI знімка = 96 × `Screen.Scaling`; редактор нормалізує вхід до 96 DPI (PNG з історії завжди читається з 96).
  - `MacPermissions` — перевірка дозволу «Запис екрана» (macOS). `MacUpdateChecker` — оновлення на Mac через GitHub API (Velopack на Mac не використовується).
  - `VlcShared` — один `LibVLC` на всю програму (прогрів у фоні); `VlcShared.ReplaceMedia` звільняє попередній `Media`.
  - `CrashReport` — `logs\last_crash.txt` при падінні + пропозиція відкрити журнал при наступному запуску.
  - `CaptureRouter` — куди йде знімок: редактор або `QuickAccessWindow` (плаваюча мініатюра; налаштування `AfterCapture`).
  - `ImageExport` — збереження PNG/JPG, папка (`SaveFolder`, за замовчуванням «Зображення/Screenshot One»), автозбереження, тимчасові файли для перетягування.
  - `ClipboardHelper` — копіювання зображення (Windows — WinForms у STA-потоці) і тексту.
  - `OcrService` + `OcrFlow` + `ToastWindow` — OCR: Windows `Windows.Media.Ocr`; Mac — Swift-утиліта `MacHelpers/ocr.swift` → `Contents/MacOS/ocr-helper` (збирає `Build-MacApp.ps1`, CI перевіряє `--selftest`). `MainWindow(ocrMode: true)` — оверлей виділення для OCR.
  - Відео: `IVideoRecorderService` має `PauseAsync/ResumeAsync` і мікрофон. Mac — пауза сегментами + `ffmpeg concat`; системний звук на Mac не пишеться (потрібен віртуальний аудіопристрій).
  - Редактор: інструменти `Ellipse`, `Line`, `Spotlight` (`SpotlightShape`), товщина `_strokeWidth`; `ShapeType` у JSON — числа, нові типи лише в кінець; `ShapeData.StrokeWidth` (0 — старі записи). `Pixelate` прибрано з панелі як дубль «Розмиття» (2.7.0), але його завантаження зі старих записів лишилось.
  - Панель редактора (2.7.0): групи з `Flyout` (`BtnShapesGroup`, `BtnLinesGroup`, `BtnDrawGroup`, `BtnHideGroup`, `BtnImageGroup`, `BtnShareGroup`), підсвітка — клас `active` (стиль у `App.axaml`), іконка групи = останній інструмент (`UpdateToolHighlight`). Колір/товщина/розмір тексту — у `ColorPalettePopup`. Однолітерні гарячі клавіші — `HandleToolShortcut`. **Без ScrollViewer у панелі.** З 2.7.2 при достатній ширині групи розгортаються в окремі кнопки (`BuildInlineToolButtons` / `UpdateToolbarMode`), вміст меню — у `Border.menucard` (шаблон SukiUI малює `FlyoutPresenter` без фону). Кнопки обрізки — у шарі `CropControlsLayer` поза масштабованим полотном (`PositionCropControls`).
  - `TrayPanelWindow` (2.8.2, на обох ОС з 2.8.4) — панель трею: плитки дій, останні 3 знімки, налаштування/оновлення/вихід. Дії — `App.RunTrayAction(TrayAction)`, спільні з меню. Перші 700 мс після показу втрата фокуса ігнорується (інакше після кліку по треєві Windows одразу закривав панель).
    Windows: лівий клік (`TrayIcon.Clicked`; Avalonia: WM_LBUTTONUP → OnClicked). Правий клік Avalonia не повідомляє, а показує власне вікно `AvaloniaTrayPopupRoot_…` з `NativeMenu` — з 2.8.5 `App.InterceptTrayMenu` (клас-обробник `Window.WindowOpenedEvent`) ховає його й відкриває панель; меню лишається запасним.
    macOS: `TrayIcon` з Avalonia **не дає події кліку** (Avalonia.Native не викликає OnClicked), тому значок — власний `MacStatusItem` (NSStatusItem через ObjC runtime: клас `SOStatusItemTarget`, action `soClick:` → `[UnmanagedCallersOnly]` C#). Лівий і правий клік — панель. Якщо створити не вдалось — запасний `TrayIcon` з меню.
    Самоперевірка: `SO_SELFTEST_TRAY=1` — через 6 с програма сама «клікає» значок (Mac — `performClick:`), у журнал пише «[SelfTest] панель трею відкрита через 2,5 с: True»; CI перевіряє це на Windows і macOS; на Windows ще й правий клік («[SelfTest] правий клік → панель: True»). Реліз (`release.yml`) викладає Mac-файли лише після такої ж самоперевірки.
  - **Меню трею не підміняти** (`_trayIcon.Menu = ...` лише один раз): на macOS Avalonia падає «The menu being updated does not match». Зміна мови — `UpdateTrayMenuTexts` (лише `Header`). Так падала Mac-версія 2.8.0–2.8.1.
  - `HotkeyService` — глобальні клавіші. Windows: low-level hook в окремому потоці; macOS: Carbon `RegisterEventHotKey`.
  - `SettingsManager` / `AppSettings` — `settings.json`, міграції (`SettingsVersion`), тема, автозапуск.
  - `UpdateWindow` — оновлення через Velopack (`GithubSource`, без токена).
  - `GoogleDriveManager` (scope `DriveFile`, файли публічні за посиланням), `PublicCloudManager` (catbox.moe).
  - `UploadConsent` — одноразове попередження про публічні посилання.
  - `AppLog` — файловий лог.
- `ScreenShot_One_Avalonia.Tests/` — xUnit, ціль `net10.0-windows10.0.19041.0`. `FeatureTests260.cs` — тести 2.6.0 (зокрема справжній OCR на Windows). `UnitTest1.cs` — переважно перевірки рядків у коді; `BehaviorTests.cs` — тести поведінки (headless Avalonia + Skia через `HeadlessAvalonia.EnsureInitialized()`; паралельний запуск вимкнено).

Дані користувача: `%LocalAppData%\Screenshot_One` (macOS: `~/Library/Application Support/Screenshot_One`). Там лежать `settings.json`, `Screenshots\`, `logs\app.log`, `token.json` (Google OAuth).

## Тема й дизайн (2.7.0, «Хвоя і м'ята»)

- Усі кольори — токени в `App.axaml` (ThemeDictionaries Dark/Light): `AppBackgroundBrush`, `PanelBrush`, `PanelBorderBrush`, `CanvasBackdropBrush`, `ToolbarIconBrush`, `MutedTextBrush`, `AccentBrush`, `OnAccentBrush`, `ActiveToolBrush`, `DangerBrush`, `ToastBrush` + перевизначені `Suki*` кольори. Акцент SukiUI — `AppTheme.ApplyAccent` (м'ятний окремо для темної/світлої).
- **Ніяких кольорів Tailwind** (zinc/slate/emerald/blue-600 тощо — автор проти) і жорстко прописаних hex у вікнах: беріть токени (`{DynamicResource ...}` або `FindResource`). Винятки: кольори малювання, бренд Google Drive, завжди темна панель запису відео.
- Кольори-токени з коду — лише через `AppTheme.Brush(this, "Key", fallback)`: звичайний `FindResource` до показу вікна не бачить ThemeDictionaries і повертає null (так кнопка Google Диска у 2.7.1 була сірою).
- Глобальні стилі: `SukiBackground` прихований, `PART_Root` вікна — суцільний `AppBackgroundBrush`, `GlassCard` непрозорий (без «скла»). Акцентна кнопка — клас `Flat`; кнопки інструментів — `Basic tool`.
- Перевіряти вигляд: headless-рендер вікон через `AppBuilder.Configure<App>().UseSkia().UseHeadless(...)` і `window.CaptureRenderedFrame()` (так робились скріншоти «було/стало»); показувати автору перед релізом.
- Іконки (2.7.3, обрав автор — варіант «B»): **хвойний скруглений квадрат (градієнт #205647→#0F2D25, тонка світла окантовка), біла «S» і м'ятне кільце «O» з точкою** — `Assets/icon.ico` (16–48 — спрощена версія без точки, товще кільце), `icon.icns`, `tray.png` / `tray-recording.png` (спрощена версія; запис — червона точка). Однакові на Windows і Mac. Генератор — PIL (Inter Display Black). Не замінювати без погодження з автором (у 2.7.0 «видошукач» не сподобався; до 2.7.3 був чорний квадрат з білим «SO»).
- Назва програми для людей — «Screenshot One» (трей, заголовки, `Product`, `CFBundleName`, `--packTitle`). Технічні імена (`Screenshot_One.exe`, `Screenshot_One.app`, папки даних `Screenshot_One`, Velopack-id `ScreenShotOne`) не міняти — зламаються оновлення й дані.

## Збірка й тести

```bash
dotnet build ScreenShot_One_Avalonia.slnx -p:Platform=x64
```

```bash
dotnet test ScreenShot_One_Avalonia.Tests -p:Platform=x64
```

- `-p:Platform=x64` обов'язковий: ScreenRecorderLib не працює з AnyCPU.
- На Linux/macOS (зокрема в хмарній сесії Claude) додавайте `-p:EnableWindowsTargeting=true`. Збірка проходить, але **тести запускаються тільки на Windows** (потрібен `Microsoft.WindowsDesktop.App`). Для перевірки тестів пушіть гілку: CI на GitHub Actions запустить їх на Windows.
- .NET 10 SDK у хмарній сесії: `apt-get install -y dotnet-sdk-10.0` (`dot.net` заблокований проксі).

### Важливо про тести

Багато тестів перевіряють **наявність конкретних рядків у вихідному коді**, наприклад `Assert.Contains("SaveToHistoryDatabase(rtb);", code)`. Перед перейменуванням чи рефакторингом шукайте рядок у `UnitTest1.cs`. Або зберігайте його, або оновлюйте тест свідомо. Тест `VideoTrimming_BundledFFmpeg_ExistsInProjectDirectory` вимагає `ScreenShot_One_Avalonia/ffmpeg.exe` (CI завантажує його сам).

## Файли, яких немає в git (репозиторій публічний)

- `ScreenShot_One_Avalonia/credentials.json` — OAuth-ключ Google Drive. Вбудовується як EmbeddedResource, якщо файл є. Для релізів зберігається в секреті GitHub `GOOGLE_CREDENTIALS_JSON`. **Ніколи не комітити.**
- `ScreenShot_One_Avalonia/ffmpeg.exe` — BtbN `ffmpeg-master-latest-win64-gpl.zip`.
- `.github_token` — лише для ручного `Deploy.ps1`.

## Реліз (деплой)

**Основний спосіб — автоматично через GitHub Actions** (`.github/workflows/release.yml`):

1. Підняти версію **в усіх місцях**:
   - `ScreenShot_One_Avalonia/ScreenShot_One_Avalonia.csproj` → `<Version>`;
   - `ScreenShot_One_Avalonia/Deploy.ps1` → `$Version`;
   - `ScreenShot_One_Avalonia/Build-MacApp.ps1` → `$Version`;
   - `ScreenShot_One_Avalonia/Info.plist` → два рядки `<string>X.Y.Z</string>`;
   - `ScreenShot_One_Avalonia/UpdateWindow.axaml.cs` → запасні рядки `"X.Y.Z"`.
2. Оновити нотатки в **трьох** файлах: `ReleaseNotes.md`, `ReleaseNotes.en.md`, `ReleaseNotes.ru.md` — нові пункти зверху (`• ...`), старі під «Зміни попередніх версій:» / «Changes in previous versions:» / «Изменения прошлых версий:».
3. Влити в `main`. Зміна `.csproj` у `main` запускає Release: тести → `dotnet publish` → `vpk pack/upload` → нотатки. Якщо реліз із такою версією вже є, workflow нічого не робить. Запустити вручну: Actions → Release → Run workflow (або `gh api -X POST repos/Oleksandr-Rosenko/Screenshot_One/actions/workflows/release.yml/dispatches -f ref=main`).
4. Теги релізів — версія без `v` (`2.5.4`). Velopack-id пакета `ScreenShotOne`.

Запасний спосіб — `Deploy.ps1` на Windows-машині автора (потрібні `credentials.json`, `ffmpeg.exe`, токен).
macOS (з 2.8.1) — job `macos` у `release.yml` (`Build-MacApp.ps1 -Arch all`) докладає до того самого релізу:
- `Screenshot_One-vX.Y.Z-macOS.pkg` — **перше встановлення**, один файл для arm64 і x64 (`MacInstaller/distribution.xml` вибирає за `hw.optional.arm64`). `preinstall` закриває й прибирає стару копію; `postinstall` знімає карантин, віддає `.app` користувачу (`chown`) і запускає програму.
- `Screenshot_One-vX.Y.Z-macOS-arm64.zip` / `-x64.zip` — для **самооновлення** (`MacUpdateChecker.SelfUpdateAsync`: завантаження → `ditto -x -k` → скрипт після виходу підміняє `Contents/` з відкатом → перезапуск). Якщо `.app` не доступний для запису — завантажує й відкриває .pkg.
- ffmpeg (статичний, ffmpeg.martin-riedl.de) кладеться в `Contents/MacOS/ffmpeg`. Підпис: кожен Mach-O + бандл, без hardened runtime. Секрети `MAC_SIGN_P12_BASE64` / `MAC_SIGN_P12_PASSWORD` — власний (не Apple) сертифікат «Screenshot One Code Signing»: стабільний підпис, macOS пам'ятає дозвіл «Запис екрана» між оновленнями. Без секретів — ad-hoc (працює, але дозвіл може злітати після оновлень). Сертифікат Apple Developer ($99/рік) автор поки не бере — тож перше відкриття .pkg вимагає «Усе одно відкрити».
- CI (`ci.yml`, job macOS) збирає все те саме з версією 0.0.0, перевіряє підпис, архітектуру ffmpeg і вміст .pkg, **встановлює .pkg і запускає програму на 25 с** (у разі падіння друкує stderr, app.log і звіт macOS).
Якщо Mac-файлів у релізі немає — повторний ручний запуск Release їх додасть.

CI (`.github/workflows/ci.yml`) — збірка й тести на Windows і збірка під macOS при кожному push у `main`/`claude/**` і в PR.

## Правила коду

- **Кінці рядків — LF** (`.gitattributes`, `.editorconfig`). Раніше файли пошкоджувались (`\r\r\n`, подвоєння порожніх рядків): не вставляйте CRLF вручну й перевіряйте diff на масові зміни порожніх рядків.
- **Без порожніх `catch { }`.** Використовуйте `AppLog.Warn(ex)` для проігнорованих помилок, `AppLog.Error(ex, "контекст")` для серйозних, `AppLog.Write("...")` замість `Debug.WriteLine` (у Release він вирізається).
- Помилки, які впливають на користувача (не зберіглось, не завантажилось), показувати через `MessageBoxManager.GetMessageBoxStandard(...)` або toast.
- Нові вікна з трею або гарячих клавіш відкривати через `ShowSingle(() => new XWindow())`.
- Жодних PNG-кодувань «туди й назад» для обрізки/копіювання — використовуйте `BitmapPixels`.
- Тимчасові `RenderTargetBitmap` звільняти (`Dispose`) одразу після використання; RTB не передавати у фонові потоки (може бути на GPU) — спершу `BitmapPixels.Crop` у звичайну пам'ять.
- Фонові задачі запускати як `SomethingAsync().FireAndForget("контекст")`, а не `async void` (окрім обробників подій).
- Перед будь-яким новим завантаженням у хмару викликати `await UploadConsent.ConfirmAsync(this)`.
- Нові поля в `AppSettings` мають безпечне значення за замовчуванням (старий `settings.json` без поля повинен читатися). Разові міграції робити через `SettingsManager.Migrate` + `CurrentSettingsVersion`.
- Платформний код ховати за `IScreenCaptureService` / `IVideoRecorderService` / `#if WINDOWS` / `OperatingSystem.IsMacOS()`.
- Без токенів і ключів у коді.
- Гарячі клавіші редактора — через `PrimaryModifier` (Ctrl на Windows, Cmd на Mac).
- Міграції налаштувань — покрокові (`if (SettingsVersion < N)`), щоб не скидати змінене користувачем.

## Робочий процес

- Зміни робити в гілці (`claude/...`), пушити, чекати зеленого CI, потім fast-forward у `main`.
- Повідомлення комітів українською; для релізу: «Версія X.Y.Z: коротко що змінилось».

## Заплановані покращення (беклог)

1. Розбити `EditorWindow.axaml.cs` на окремі класи: інструменти малювання (`IShapeTool`), undo/redo, обрізка, буфер обміну, завантаження. Або перейти на MVVM.
2. Замінити решту тестів «код містить рядок» у `UnitTest1.cs` на тести поведінки (`BehaviorTests.cs`).
3. Історія займає багато місця: два PNG на скріншот + Base64 вставок у JSON. Варіанти: зберігати оригінал і фігури, а підсумкову картинку рендерити за потреби; вставки тримати окремими файлами.
4. Відеоредактор: ~20 «магічних» `Task.Delay` маскують гонки VLC — замінити на події `MediaPlayer` (Playing, LengthChanged, EndReached). Робити обережно, з ручною перевіркою.
5. Mac: ScreenCaptureKit уже працює через `capture-helper` (2.8.3), `CGDisplayCreateImage` і `screencapture` лишились запасними. Далі: виключати власні вікна програми зі знімка (`SCContentFilter` з `excludingApplications` / `exceptingWindows`) — прибере мініатюру з наступних знімків на Mac; знімок окремого вікна (`SCContentFilter(desktopIndependentWindow:)`); коли macOS 14 стане мінімальною — прибрати `MacCoreGraphics`.
6. Mac: підпис і нотаризація сертифікатом Apple Developer (прибере крок «Усе одно відкрити» і дасть Velopack на Mac).

## Наступні функції (порівняння з CleanShot X / Snagit, погоджено з автором)

Зроблено в 2.6.0: «Зберегти як» + автозбереження, мікрофон і пауза, плаваюча мініатюра, нові фігури + товщина, OCR.
Далі — по черзі:
1. Знімок окремого вікна (клік по вікну; опційно тінь / прозорий фон).
2. Експорт відео в GIF (ffmpeg уже є: palettegen/paletteuse).
3. Закріпити скріншот поверх усіх вікон («стікер»).
4. Красивий фон / рамка (відступи, градієнт, тінь, закруглені кути).
5. Показ натиснутих клавіш під час запису.
6. Пошук і теги в історії.
7. Більше місць для завантаження: Dropbox, OneDrive, власний сервер.
8. Ховати іконки робочого столу під час знімка.
9. Прокручуваний знімок (довгі сторінки) — складно.
10. Вебкамера поверх запису — складно.
Мініатюра на Mac поки потрапляє в наступні знімки (на Windows виключена через `SetWindowDisplayAffinity`).

## Історія ключових змін

- 2.8.5 — правий клік по треєві теж відкриває панель (Windows); нотатки до релізу трьома мовами, вікно оновлення показує їх мовою програми.
- 2.8.4 — панель трею на Mac (власний NSStatusItem) і виправлення миттєвого закриття на Windows; самоперевірка панелі в CI на обох ОС.
- 2.8.3 — знімок екрана на macOS 14+ через ScreenCaptureKit (`capture-helper`), CoreGraphics і screencapture — запасні.
- 2.8.2 — виправлено падіння Mac на старті (підміна меню трею при зміні мови); панель трею з плитками на Windows; запуск програми в CI на macOS.
- 2.8.1 — macOS: універсальний інсталятор .pkg, вбудований ffmpeg, самооновлення без пароля, стабільний підпис (якщо є секрети).
- 2.8.0 — локалізація: українська, англійська, російська; автовибір за мовою ОС, вибір у налаштуваннях.
- 2.7.3 — нова іконка «SO» (варіант B, обрав автор).
- 2.7.2 — розгорнута панель редактора, суцільні меню, кнопки обрізки поверх полотна, справжнє центрування історії, кнопка Google Диска, перейменування ярлика Windows (`WindowsShell`).
- 2.7.1 — повернуто іконку «SO», назва «Screenshot One», вікно налаштувань у межах екрана, швидке відкриття й центрування історії.
- 2.7.0 — редизайн «Хвоя і м'ята»: палітра-токени, суцільні вікна, згрупована панель редактора без прокрутки + гарячі клавіші, історія-галерея, нові налаштування, нові іконки (у т.ч. icon.icns для Mac), прибрано дубль піксельного розмиття.
- 2.6.0 — OCR (Windows/Mac), плаваюча мініатюра, «Зберегти як» і автозбереження, нові інструменти (еліпс, лінія, піксельне розмиття, прожектор, товщина), мікрофон і пауза у відео, Cmd-скорочення на Mac.
- 2.5.5 — захоплення й обрізка в пам'яті (Win/Mac), нативний знімок CoreGraphics на Mac, DPI/Retina-виправлення, фонове збереження історії, звільнення пам'яті в редакторі, спільний LibVLC, Mac: дозвіл «Запис екрана», LaunchAgent-автозапуск, оновлення через GitHub, архіви в автодеплої; звіт про падіння; тести поведінки.
- 2.5.4 — відновлено форматування (зайві порожні рядки); `AppLog`; унікальні ID історії; `ShowSingle`; `UploadConsent`; CI та автоматичний деплой через GitHub Actions; README.
