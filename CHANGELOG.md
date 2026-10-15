# Changelog

Each version's section becomes its GitHub release notes (`.github/workflows/release.yml`), and Pylon shows it in
"What's new" after updating — the part between `<!-- ru -->` and `<!-- en -->` in Russian, the rest in English.

## 1.6.0

<!-- ru -->
**Музыка**
- **Перевод текста песни** мелкой строкой под оригиналом — если он есть в файле (TTML из Apple Music с переводами, двуязычный LRC). Выключается в Настройки → Интерфейс → Текст песни.
- **Меню «Текст песни»** (правый клик в режиме музыки): найти текст под другим названием (Spicy Lyrics, затем LRCLIB), открыть свой файл .lrc / .ttml, сдвинуть синхронизацию раньше или позже на 0,25 с. Сдвиг запоминается для каждого трека.
- **Обложки из интернета** для песен без своей: ищутся в iTunes по исполнителю и альбому, видны в окне и в медиа-панели Windows. Работает при включённом «Загружать названия и обложки из интернета».
- **Умные плейлисты**: «Часто слушаю», «Недавно слушал», «Недавно добавлено».
- **«Играть следующим»** в меню любых файлов и папок; в «Сейчас играет» строки перетаскиваются мышью.
- **Одинаковая громкость без тегов**: «Выравнивание громкости → Измеренная». Pylon сам измеряет каждую песню (заодно с волной на шкале), а следующую в очереди — заранее, поэтому она сразу звучит на своём уровне. Плавный переход это учитывает.
- **Текст в мини-плеере**: поверх обложки строка, которая сейчас поётся, и следующая.

<!-- en -->
**Music**
- **Lyrics translations** in a small line under the original, when the file carries them (Apple Music TTML with translations, bilingual LRC). Turn off in Settings → Interface → Lyrics.
- **Lyrics menu** (right-click in music mode): find lyrics under another name (Spicy Lyrics, then LRCLIB), open your own .lrc / .ttml, move the timing 0.25 s sooner or later. The offset is remembered per track.
- **Cover art from the internet** for songs without any: looked up on iTunes by artist and album, shown in the window and the Windows media flyout. Needs "Fetch titles and cover art from the internet".
- **Smart playlists**: Most Played, Recently Played, Recently Added.
- **Play Next** in every file and folder menu; drag rows in Now Playing to reorder.
- **Even loudness without tags**: Replay gain → Measured. Pylon measures each song itself (along with the seek bar waveform), and the next one in the queue ahead of time, so it starts at its level. The crossfade keeps it.
- **Lyrics in the miniplayer**: the line being sung and the next one over the cover.

## 1.5.0

<!-- ru -->
**Новое**
- **Иконка в трее** (Настройки → Интерфейс): свёрнутый Pylon прячется в трей; щелчок возвращает окно, правый щелчок — пауза, следующий, предыдущий, выход. Можно запускаться сразу в трее.
- **Карточка трека в углу экрана** при смене песни: обложка, название, исполнитель. Когда показывать — никогда, если окно свёрнуто, или всегда; длительность настраивается.
- **Пауза, когда звучит другое приложение** (Настройки → Воспроизведение): Pylon ставит паузу, пока в другом приложении есть звук (по его индикатору уровня, а не просто открытому потоку), и продолжает через три секунды тишины.
- Закреплённые папки на боковой панели — плитки с первой буквой и своим цветом; порядок меняется через «Выше» и «Ниже».

**Улучшения**
- Спектр подстраивается под громкость записи: тихие мастеринги больше не выглядят плоскими.
- Волна на шкале строится и для очень длинных файлов (аудиокниги): 70-минутный файл готов примерно за 25 секунд, раньше он оставался с декоративной волной.
- Превью на шкале перемотки декодирует видеокарта и прыгает к ключевым кадрам; уже увиденные кадры показываются сразу.
- Убраны настройки, которые ничего не делали (статистика, скины, дерево плейлиста и т. п.).

<!-- en -->
**New**
- **Tray icon** (Settings → Interface): minimizing hides Pylon to the tray; click brings it back, right-click for pause, next, previous, quit. Can start in the tray.
- **Track card in the corner of the screen** when the song changes: cover, title, artist. Never / when minimized / always, with an adjustable duration.
- **Pause while another app plays sound** (Settings → Playback): Pylon pauses while another app makes sound (by its level meter, not just an open stream) and resumes after three quiet seconds.
- Pinned sidebar folders are tiles with their first letter in their own color; reorder them with Move Up / Move Down.

**Improvements**
- The spectrum follows the recording's loudness, so quiet masters no longer look flat.
- The seek bar waveform works for very long files (audiobooks): a 70-minute file is ready in about 25 seconds instead of staying decorative.
- Seek bar previews decode on the GPU and jump to keyframes; frames already seen show at once.
- Settings that did nothing are gone (statistics, skins, playlist tree and the like).

## 1.4.0

<!-- ru -->
**Этап 3**
- **Видео с сайтов**: YouTube, Twitch, VK, Rutube, SoundCloud и ещё около 1800 сайтов. Вставьте ссылку («Открыть сетевой поток» или перетащите), и Pylon сыграет её через yt-dlp. Он скачивается при первой ссылке (Pylon спросит) и обновляется сам. Плейлист или канал попадает в очередь целиком; качество берётся из «Предпочитаемое качество видео». Если сайт просит доказать, что вы не бот, включите «Входить на сайты с cookies из» (Настройки → Сеть).
- **Плавный переход между песнями** (Настройки → Воспроизведение): следующая песня нарастает, пока текущая затихает. У каждой песни своя громкость, так что переход ровный и не трогает общий ползунок громкости.

**Исправления**
- Значок паузы и воспроизведения в режиме музыки появляется на обложке, а не поверх текста песни.

<!-- en -->
**Stage 3**
- **Videos from websites**: YouTube, Twitch, VK, Rutube, SoundCloud and about 1,800 other sites. Paste a link (Open Network Stream, or drop it on the window) and Pylon plays it through yt-dlp, downloaded on the first link (Pylon asks first) and kept up to date. A playlist or channel joins the queue whole; quality follows "Preferred video quality". If a site wants proof you're not a bot, turn on "Sign in to websites with cookies from" (Settings → Network).
- **Crossfade between songs** (Settings → Playback): the next song fades in while the current one fades out. Each song has its own loudness, so the blend is even and the volume slider stays put.

**Fixes**
- In music mode the play / pause badge appears on the cover, not over the lyrics.

## 1.3.1

<!-- ru -->
**Исправления**
- **Аудиоэффекты правда работают.** Компрессор, пространственный звук, сдвиг тона, хорус, караоке, нормализация и ночной режим не включались на обычном выводе звука. Расширение стерео убрано: в libVLC 3 оно не запускается вообще.
- **Нет серых артефактов после перемотки** в тяжёлых видео (например, записи игр 1440p60 HEVC), ни в плеере, ни в превью на полосе перемотки.
- **Spicy Lyrics находит песни сам**: Spotify id ищется по исполнителю и названию через ListenBrainz, Spotify-приложение больше не нужно. Песни, для которых раньше «ничего не нашлось», проверятся заново.

**Новое**
- **Текст песни двигается как в Spicy Lyrics**: у каждого слова пружинные масштаб, подъём и свечение; долгие слоги поются по буквам; в паузах дышат три точки; строки размываются по расстоянию и проясняются под курсором.
- **Папки на боковой панели**: правый клик по папке → «Закрепить на боковой панели».
- **Меню для музыки**: правый клик в режиме музыки — текст песни, добавить в плейлист, показать в проводнике, конвертировать. У «Песен» в библиотеке тоже есть меню.
- Настройки текста песен вынесены в свой раздел «Текст песни» (Настройки → Интерфейс).
- В Проводнике у превью видео — плёнка и значок Pylon в углу, у обложек музыки — значок Pylon.

<!-- en -->
**Fixes**
- **Audio effects really work.** Compressor, spatial audio, pitch, chorus, karaoke, normalizer and night mode didn't engage on the normal audio output. Stereo widening is gone: libVLC 3 can't run it at all.
- **No grey smears after seeking** in heavy video (such as 1440p60 HEVC game recordings), in the player and in the seek bar preview.
- **Spicy Lyrics finds songs by itself**: the Spotify id comes from artist and title through ListenBrainz, no Spotify app needed. Songs that came up empty before are checked again.

**New**
- **Lyrics move like Spicy Lyrics**: every word springs in scale, lift and glow; long syllables are sung letter by letter; three dots breathe in the gaps; lines blur by distance and clear under the pointer.
- **Pin folders to the sidebar**: right-click a folder → Pin to Sidebar.
- **Music menu**: right-click in music mode for lyrics, Add to Playlist, Show in Explorer, Convert. "Songs" in the library has a menu too.
- Lyrics settings have their own "Lyrics" heading (Settings → Interface).
- In Explorer, video thumbnails get film sprockets and a Pylon badge; album art gets the badge.

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
