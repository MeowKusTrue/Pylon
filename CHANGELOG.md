# Changelog

Each version's section becomes its GitHub release notes (`.github/workflows/release.yml`), and Pylon shows it in
"What's new" after updating — the part between `<!-- ru -->` and `<!-- en -->` in Russian, the rest in English.

## 1.3.0

<!-- ru -->
**Новое**
- **Дельта-обновления.** Pylon перешёл на Velopack: при обновлении скачиваются только изменившиеся файлы, обычно несколько сотен КБ вместо 130 МБ. Перезапуск сразу в новую версию.
- **Своя иконка для каждого формата** в Проводнике: MKV, MP4, AVI, MP3, FLAC, OPUS и ещё 27. Превью кадров видео и обложки Windows рисует поверх, как раньше.
- Портативная версия теперь тоже обновляется сама.

**Переход**
- Установка 1.0–1.2 обновится как обычно: новая версия встанет в `%LocalAppData%\Pylon` и удалит старую копию. Настройки, библиотека и ярлык на рабочем столе остаются.

**Исправления**
- Второй запуск Pylon больше не падает, если первая копия запущена с другими правами.

<!-- en -->
**New**
- **Delta updates.** Pylon now uses Velopack: an update downloads only the files that changed, usually a few hundred KB instead of 130 MB, and restarts straight into the new version.
- **An icon for every format** in Explorer: MKV, MP4, AVI, MP3, FLAC, OPUS and 27 more. Windows still draws video frames and album art on top.
- The portable copy updates itself too.

**Moving over**
- A 1.0–1.2 install updates as usual: the new version goes to `%LocalAppData%\Pylon` and removes the old copy. Settings, library and the desktop shortcut stay.

**Fixes**
- Starting Pylon a second time no longer crashes when the first copy runs with different rights.

## 1.2.0

<!-- ru -->
**Новое**
- **Конвертация** (меню «⋯» → «Конвертировать», `Ctrl+R`): MP3, M4A, FLAC, Opus, WAV, MP4 и GIF, сразу пачкой файлов, с прогрессом по каждому.
- **Сохранить отрезок A-B**: отметьте петлю A-B, затем правый клик → «Воспроизведение» → «Сохранить отрезок A-B». Можно мгновенно, без перекодирования, или в GIF.
- **Трансляция на ТВ и телефон** (меню «⋯»): плеер раздаёт то, что играет, по локальной сети. QR-код ведёт на страницу с кнопкой «Открыть в VLC», адрес потока можно вставить в VLC или Kodi на телевизоре. Воспроизведение у вас при этом продолжается.

<!-- en -->
**New**
- **Convert** (⋯ menu → Convert, `Ctrl+R`): MP3, M4A, FLAC, Opus, WAV, MP4 and GIF, many files at once with progress for each.
- **Save an A-B clip**: set the A-B loop, then right-click → Playback → Save A-B Clip. Instant without re-encoding, or as a GIF.
- **Stream to TV / phone** (⋯ menu): Pylon shares what's playing on your local network. The QR code opens a page with "Open in VLC"; the stream address works in VLC or Kodi on a TV. Your own playback keeps going.

## 1.1.1

<!-- ru -->
**Исправления**
- Работают все аудиоэффекты: компрессор, пространственный звук, расширение стерео, сдвиг тона, хорус, караоке и нормализация раньше молча не включались.
- Текст песни больше не обрывается и не уезжает, активная строка стоит по центру, колесо мыши прокручивает текст.
- Боковые панели открываются и закрываются плавно, без рывка в конце.
- Треки переключаются примерно в пять раз быстрее.
- Второй экземпляр Pylon больше не падает при запуске.
- Значок «плей» стоит ровно по центру.

**Новое**
- Тексты из Spicy Lyrics с синхронизацией по словам (нужен ключ API в настройках интерфейса).
- Значок плея или паузы в центре экрана при клике по видео или музыке.
- Проверка обновлений при каждом запуске и окно «Что нового» после обновления.

<!-- en -->
**Fixes**
- Every audio effect works: the compressor, spatial audio, stereo widening, pitch shift, chorus, karaoke and normalizer used to do nothing.
- Lyrics no longer cut off or drift away; the current line stays centered and the wheel scrolls the text.
- Side panels open and close smoothly, without a jump at the end.
- Switching tracks is about five times faster.
- A second copy of Pylon no longer crashes on start.
- The play icon is centered.

**New**
- Word-synced lyrics from Spicy Lyrics (add an API key in Interface settings).
- A play / pause badge in the middle of the picture when you click video or music.
- Updates are checked on every start, and "What's new" appears after updating.

## 1.1.0

<!-- ru -->
Тексты песен в стиле Spicy Lyrics, главы и закладки на шкале, RTX Super Resolution и RTX Video HDR, 360°-видео,
медиапанель Windows, размытый фон вокруг видео, живые фоны, плавный ползунок.

<!-- en -->
Lyrics in the style of Spicy Lyrics, chapters and bookmarks on the seek bar, RTX Super Resolution and RTX Video HDR,
360° video, the Windows media session, a blurred backdrop around video, live backgrounds, a smooth seek bar.
