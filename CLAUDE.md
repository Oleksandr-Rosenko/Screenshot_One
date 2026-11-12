# Screenshot One — пам'ятка для AI-асистентів (Claude Code, Antigravity тощо)

Програма для скріншотів і запису екрана для Windows і macOS. Avalonia 12 + SukiUI, .NET 10.
Інтерфейс, коментарі в коді, повідомлення комітів і нотатки до релізів пишемо **українською**.

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
    Windows: GDI `CopyFromScreen` + пряме копіювання пікселів (`LockBits`). Mac: `MacCoreGraphics` (CoreGraphics `CGDisplayCreateImage`), запасний шлях — `/usr/sbin/screencapture`.
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
- Іконки: **фірмовий знак автора — чорний квадрат з білим «SO»** (`Assets/tray.png`, `tray-recording.png` з червоною точкою, `icon.ico` 16–256, `icon.icns`). Однаковий на Windows і Mac. Не замінювати на інші знаки (у 2.7.0 спроба «видошукача» автору не сподобалась).
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
2. Оновити `ScreenShot_One_Avalonia/ReleaseNotes.md`: нові пункти зверху (`• ...`), старі під «Зміни попередніх версій:».
3. Влити в `main`. Зміна `.csproj` у `main` запускає Release: тести → `dotnet publish` → `vpk pack/upload` → нотатки. Якщо реліз із такою версією вже є, workflow нічого не робить. Запустити вручну: Actions → Release → Run workflow (або `gh api -X POST repos/Oleksandr-Rosenko/Screenshot_One/actions/workflows/release.yml/dispatches -f ref=main`).
4. Теги релізів — версія без `v` (`2.5.4`). Velopack-id пакета `ScreenShotOne`.

Запасний спосіб — `Deploy.ps1` на Windows-машині автора (потрібні `credentials.json`, `ffmpeg.exe`, токен).
macOS-архіви (`Screenshot_One-vX.Y.Z-macOS-arm64.zip` / `-x64.zip`) збирає job `macos` у `release.yml` (`Build-MacApp.ps1 -Arch all`) і докладає до того самого релізу. Якщо архівів у релізі немає — повторний ручний запуск Release їх додасть.

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
5. Mac: нативний знімок через `CGDisplayCreateImage` позначений Apple як застарілий (macOS 14+); на перспективу — ScreenCaptureKit. Зараз при збої автоматично працює `screencapture`.
6. Mac: підпис і нотаризація `.app` (зараз користувач знімає карантин `xattr -cr`); вбудувати `ffmpeg` у бандл.

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

- 2.7.2 — розгорнута панель редактора, суцільні меню, кнопки обрізки поверх полотна, справжнє центрування історії, кнопка Google Диска, перейменування ярлика Windows (`WindowsShell`).
- 2.7.1 — повернуто іконку «SO», назва «Screenshot One», вікно налаштувань у межах екрана, швидке відкриття й центрування історії.
- 2.7.0 — редизайн «Хвоя і м'ята»: палітра-токени, суцільні вікна, згрупована панель редактора без прокрутки + гарячі клавіші, історія-галерея, нові налаштування, нові іконки (у т.ч. icon.icns для Mac), прибрано дубль піксельного розмиття.
- 2.6.0 — OCR (Windows/Mac), плаваюча мініатюра, «Зберегти як» і автозбереження, нові інструменти (еліпс, лінія, піксельне розмиття, прожектор, товщина), мікрофон і пауза у відео, Cmd-скорочення на Mac.
- 2.5.5 — захоплення й обрізка в пам'яті (Win/Mac), нативний знімок CoreGraphics на Mac, DPI/Retina-виправлення, фонове збереження історії, звільнення пам'яті в редакторі, спільний LibVLC, Mac: дозвіл «Запис екрана», LaunchAgent-автозапуск, оновлення через GitHub, архіви в автодеплої; звіт про падіння; тести поведінки.
- 2.5.4 — відновлено форматування (зайві порожні рядки); `AppLog`; унікальні ID історії; `ShowSingle`; `UploadConsent`; CI та автоматичний деплой через GitHub Actions; README.
