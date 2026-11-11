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
  - `VideoControlWindow`, `VideoRegionWindow`, `VideoRecorderManager` — запис відео. На Windows ScreenRecorderLib, на macOS ffmpeg. Інтерфейс `IVideoRecorderService`.
  - `VideoEditorWindow` — відеоплеєр (LibVLCSharp) і обрізка (ffmpeg).
  - `ScreenCaptureService` (`IScreenCaptureService`: Windows/Mac/Linux), `ScreenService`, `PlatformHelper` — платформна логіка.
  - `HotkeyService` — глобальні клавіші. Windows: low-level hook в окремому потоці; macOS: Carbon `RegisterEventHotKey`.
  - `SettingsManager` / `AppSettings` — `settings.json`, міграції (`SettingsVersion`), тема, автозапуск.
  - `UpdateWindow` — оновлення через Velopack (`GithubSource`, без токена).
  - `GoogleDriveManager` (scope `DriveFile`, файли публічні за посиланням), `PublicCloudManager` (catbox.moe).
  - `UploadConsent` — одноразове попередження про публічні посилання.
  - `AppLog` — файловий лог.
- `ScreenShot_One_Avalonia.Tests/UnitTest1.cs` — xUnit, ціль `net10.0-windows`.

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
macOS-архіви збираються вручну `Build-MacApp.ps1` + `package_mac_zip.py`; автоматичний деплой поки робить тільки Windows.

CI (`.github/workflows/ci.yml`) — збірка й тести на Windows і збірка під macOS при кожному push у `main`/`claude/**` і в PR.

## Правила коду

- **Кінці рядків — LF** (`.gitattributes`, `.editorconfig`). Раніше файли пошкоджувались (`\r\r\n`, подвоєння порожніх рядків): не вставляйте CRLF вручну й перевіряйте diff на масові зміни порожніх рядків.
- **Без порожніх `catch { }`.** Використовуйте `AppLog.Warn(ex)` для проігнорованих помилок, `AppLog.Error(ex, "контекст")` для серйозних, `AppLog.Write("...")` замість `Debug.WriteLine` (у Release він вирізається).
- Помилки, які впливають на користувача (не зберіглось, не завантажилось), показувати через `MessageBoxManager.GetMessageBoxStandard(...)` або toast.
- Нові вікна з трею або гарячих клавіш відкривати через `ShowSingle(() => new XWindow())`.
- Перед будь-яким новим завантаженням у хмару викликати `await UploadConsent.ConfirmAsync(this)`.
- Нові поля в `AppSettings` мають безпечне значення за замовчуванням (старий `settings.json` без поля повинен читатися). Разові міграції робити через `SettingsManager.Migrate` + `CurrentSettingsVersion`.
- Платформний код ховати за `IScreenCaptureService` / `IVideoRecorderService` / `#if WINDOWS` / `OperatingSystem.IsMacOS()`.
- Без токенів і ключів у коді.

## Робочий процес

- Зміни робити в гілці (`claude/...`), пушити, чекати зеленого CI, потім fast-forward у `main`.
- Повідомлення комітів українською; для релізу: «Версія X.Y.Z: коротко що змінилось».

## Заплановані покращення (беклог)

1. Розбити `EditorWindow.axaml.cs` на окремі класи: інструменти малювання (`IShapeTool`), undo/redo, обрізка, буфер обміну, завантаження. Або перейти на MVVM.
2. Замінити тести «код містить рядок» на тести поведінки (серіалізація, обрізка, undo/redo, міграції).
3. Історія займає багато місця: два PNG на скріншот + Base64 вставок у JSON. Варіанти: зберігати оригінал і фігури, а підсумкову картинку рендерити за потреби; вставки тримати окремими файлами.
4. Зберігати в історію у фоновому потоці, щоб не блокувати UI на великих екранах.
5. `WindowsScreenCaptureService.CaptureRect` кодує PNG і декодує назад: копіювати пікселі напряму у `WriteableBitmap` через `LockBits`.
6. Додати в `release.yml` збірку й публікацію macOS-архівів (`osx-arm64`, `osx-x64`).
7. `async void` поза обробниками подій (наприклад `PasteSecondImageFromClipboard`) переробити на `async Task`.

## Історія ключових змін

- 2.5.4 — відновлено форматування (зайві порожні рядки); `AppLog`; унікальні ID історії; `ShowSingle`; `UploadConsent`; CI та автоматичний деплой через GitHub Actions; README.
