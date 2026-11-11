# Screenshot One — пам'ятка для AI-асистентів (Claude Code, Antigravity тощо)

Програма для скріншотів і запису екрана для Windows і macOS. Avalonia 12 + SukiUI, .NET 10.
Інтерфейс, коментарі в коді, повідомлення комітів і нотатки до релізів пишемо **українською**.

## Структура

Розв'язок `ScreenShot_One_Avalonia.slnx`: два проєкти.

- `ScreenShot_One_Avalonia/` — програма (`AssemblyName` = `Screenshot_One`). Дві цілі: `net10.0-windows` і `net10.0` (macOS).
  - `Program.cs` — точка входу, Velopack-хук, глобальні обробники помилок.
  - `App.axaml.cs` — трей-меню, гарячі клавіші, `ShowSingle<T>()` (показ вікна в одному екземплярі), фонова перевірка оновлень.
  - `MainWindow` — оверлей виділення зони для скріншота.
  - `EditorWindow` — редактор скріншотів (~1900 рядків code-behind): фігури (`ShapeType`), undo/redo, обрізка, вставка, копіювання, завантаження.
  - `HistoryWindow` + `HistoryManager` — історія. Файли `{id}_original.png`, `{id}_flattened.png`, `{id}_meta.json`; ліміт 500; ID `yyyyMMdd_HHmmss_fff`.
    Редактор зберігає через `HistoryManager.ReserveId` + `SaveScreenshotInBackground` (фонова черга; `_flattened.png` пишеться останнім і атомарно). Перед виходом/оновленням — `HistoryManager.WaitForPendingSaves`.
  - `VideoControlWindow`, `VideoRegionWindow`, `VideoRecorderManager` — запис відео. На Windows ScreenRecorderLib, на macOS ffmpeg. Інтерфейс `IVideoRecorderService`.
  - `VideoEditorWindow` — відеоплеєр (LibVLCSharp) і обрізка (ffmpeg).
  - `ScreenCaptureService` (`IScreenCaptureService`: Windows/Mac/Linux), `ScreenService`, `PlatformHelper` — платформна логіка.
    Windows: GDI `CopyFromScreen` + пряме копіювання пікселів (`LockBits`). Mac: `MacCoreGraphics` (CoreGraphics `CGDisplayCreateImage`), запасний шлях — `/usr/sbin/screencapture`.
  - `BitmapPixels` — обрізка / копіювання / зміна DPI бітмапів у пам'яті (без PNG). DPI знімка = 96 × `Screen.Scaling`; редактор нормалізує вхід до 96 DPI (PNG з історії завжди читається з 96).
  - `MacPermissions` — перевірка дозволу «Запис екрана» (macOS). `MacUpdateChecker` — оновлення на Mac через GitHub API (Velopack на Mac не використовується).
  - `VlcShared` — один `LibVLC` на всю програму (прогрів у фоні); `VlcShared.ReplaceMedia` звільняє попередній `Media`.
  - `CrashReport` — `logs\last_crash.txt` при падінні + пропозиція відкрити журнал при наступному запуску.
  - `HotkeyService` — глобальні клавіші. Windows: low-level hook в окремому потоці; macOS: Carbon `RegisterEventHotKey`.
  - `SettingsManager` / `AppSettings` — `settings.json`, міграції (`SettingsVersion`), тема, автозапуск.
  - `UpdateWindow` — оновлення через Velopack (`GithubSource`, без токена).
  - `GoogleDriveManager` (scope `DriveFile`, файли публічні за посиланням), `PublicCloudManager` (catbox.moe).
  - `UploadConsent` — одноразове попередження про публічні посилання.
  - `AppLog` — файловий лог.
- `ScreenShot_One_Avalonia.Tests/` — xUnit, ціль `net10.0-windows`. `UnitTest1.cs` — переважно перевірки рядків у коді; `BehaviorTests.cs` — тести поведінки (headless Avalonia + Skia через `HeadlessAvalonia.EnsureInitialized()`; паралельний запуск вимкнено).

Дані користувача: `%LocalAppData%\Screenshot_One` (macOS: `~/Library/Application Support/Screenshot_One`). Там лежать `settings.json`, `Screenshots\`, `logs\app.log`, `token.json` (Google OAuth).

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

## Історія ключових змін

- 2.5.5 — захоплення й обрізка в пам'яті (Win/Mac), нативний знімок CoreGraphics на Mac, DPI/Retina-виправлення, фонове збереження історії, звільнення пам'яті в редакторі, спільний LibVLC, Mac: дозвіл «Запис екрана», LaunchAgent-автозапуск, оновлення через GitHub, архіви в автодеплої; звіт про падіння; тести поведінки.
- 2.5.4 — відновлено форматування (зайві порожні рядки); `AppLog`; унікальні ID історії; `ShowSingle`; `UploadConsent`; CI та автоматичний деплой через GitHub Actions; README.
