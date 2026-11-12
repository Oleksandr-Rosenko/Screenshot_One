# Screenshot One

Програма для скріншотів і запису екрана для **Windows** і **macOS** на [Avalonia](https://avaloniaui.net/) (.NET 10).

## Можливості

- Скріншот зони, активного екрана або всіх екранів одночасно (також із затримкою 3 с).
- Плаваюча мініатюра після знімка: копіювати, зберегти, редагувати або перетягнути файл у будь-яку програму.
- Розпізнавання тексту (OCR) з будь-якої зони екрана — без інтернету.
- Редактор: стрілки, лінії, прямокутники, еліпси, нумеровані мітки, олівець, маркер, текст, розмиття, піксельне розмиття, прожектор, обрізка, вставка зображень, товщина ліній, undo/redo.
- Збереження у файл (PNG / JPG) і автозбереження в папку.
- Запис відео екрана або вибраної зони зі звуком системи й мікрофоном, з паузою. Вбудований відеоредактор з обрізкою та вирізанням фрагментів.
- Історія скріншотів і відео з можливістю повторного редагування.
- Завантаження на Google Drive або catbox.moe з копіюванням посилання.
- Світла й темна тема, автозапуск, автоматичні оновлення через [Velopack](https://velopack.io/).

## Встановлення

- **Windows:** `ScreenShotOne-win-Setup.exe` з [останнього релізу](https://github.com/Oleksandr-Rosenko/Screenshot_One/releases/latest). Оновлення ставляться самі.
- **macOS:** `Screenshot_One-vX.Y.Z-macOS.pkg` з [останнього релізу](https://github.com/Oleksandr-Rosenko/Screenshot_One/releases/latest) — один файл для Apple Silicon і Intel.
  1. Відкрийте .pkg. Перший раз macOS скаже, що не може перевірити розробника (програма не підписана сертифікатом Apple): «Системні параметри → Конфіденційність і безпека → Усе одно відкрити».
  2. «Продовжити → Встановити» — програма сама з'явиться в «Програмах» і запуститься.
  3. Дозвольте «Запис екрана», коли програма попросить, і перезапустіть її.
  Наступні оновлення програма ставить сама. ffmpeg для відео вже всередині.

## Гарячі клавіші за замовчуванням

| Дія | Windows | macOS |
|---|---|---|
| Скріншот зони | `PrtSc` | `⌘⇧7` |
| Активний екран | `Shift+PrtSc` | `⌘⇧8` |
| Усі екрани | `Ctrl+PrtSc` | `⌘⇧9` |
| Запис відео | `Ctrl+Shift+PrtSc` | `⌘⇧0` |
| Розпізнати текст (OCR) | `Ctrl+Alt+PrtSc` | `⌘⇧2` |

Комбінації змінюються в «Налаштуваннях» (іконка в треї).

> ⚠️ Файли, завантажені на Google Drive чи catbox.moe, доступні **усім, хто має посилання**.

## Де зберігаються дані

`%LocalAppData%\Screenshot_One` (на macOS `~/Library/Application Support/Screenshot_One`):

- `settings.json` — налаштування;
- `Screenshots\` — історія (до 500 останніх скріншотів);
- `logs\app.log` — журнал помилок (додайте його до опису проблеми).

## Збірка

Потрібен [.NET 10 SDK](https://dotnet.microsoft.com/download).

Файли, яких немає в git:

- `ScreenShot_One_Avalonia/ffmpeg.exe` — для обрізки відео на Windows ([збірки ffmpeg](https://github.com/BtbN/FFmpeg-Builds/releases)). На macOS ffmpeg вбудовується в `.app` під час збірки (`Build-MacApp.ps1`).
- `ScreenShot_One_Avalonia/credentials.json` — OAuth-ключ Google Drive (Desktop app) з Google Cloud Console. Без нього програма збирається, але завантаження на Drive не працює.

```bash
dotnet build ScreenShot_One_Avalonia.slnx -p:Platform=x64
```

```bash
dotnet test ScreenShot_One_Avalonia.Tests -p:Platform=x64
```

### Реліз

- Windows: `ScreenShot_One_Avalonia/Deploy.ps1`. Перед запуском підніміть `$Version` у скрипті та `<Version>` у `.csproj`. Потрібен токен GitHub у змінній `GITHUB_TOKEN` або у файлі `.github_token`.
- macOS: `ScreenShot_One_Avalonia/Build-MacApp.ps1`.

Що нового — у [`ReleaseNotes.md`](ScreenShot_One_Avalonia/ReleaseNotes.md).
