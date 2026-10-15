# Changelog

Each version's section becomes its GitHub release notes (`.github/workflows/release.yml`), and Pylon shows it in
"What's new" after updating — the part between `<!-- ru -->` and `<!-- en -->` in Russian, the rest in English.

## 1.9.4

<!-- ru -->
**Новое**
- **Плеер по умолчанию одной кнопкой** — карточка в самом верху Настройки → Общие. Pylon сам забирает все форматы, для которых вы ещё не выбирали программу, а для остальных сразу открывает свою страницу в «Приложениях по умолчанию». Там же видно, сколько форматов уже открывается в Pylon.
- **Подробное скачивание обновления**: размер, скорость и оставшееся время. Этап сборки новой версии из скачанных изменений теперь виден и не выглядит зависанием на 70%. Скачивание можно отменить.
- **Играющий трек в боковой панели** выделен подложкой и «живым эквалайзером» на обложке.
- **Полоса прокрутки у текста песни**: появляется при наведении и прокрутке, её можно тянуть.
- **Новые стили анимации текста** (Настройки → Интерфейс): «Караоке» — простая заливка, легче; «По строкам» — строка загорается целиком, самый лёгкий.
- **Визуализатор на паузе** не исчезает, а тихо «дышит».
- **Переводы на Weblate**: интерфейс можно переводить прямо в браузере; кнопка «Помочь с переводом» — в Настройки → Общие.

**Исправления**
- Текст песни больше не пропадает: после повторной загрузки того же текста все строки становились прозрачными.
- Визуализатор в настройках и в контекстном меню — теперь одна и та же настройка («Выкл / Столбики / Зеркало / Волна / Пульс»), на видео его больше нет. Встроенная визуализация VLC подписана отдельно.
- Понятные названия шагов перемотки: какой прыжок на какие клавиши.

<!-- en -->
**New**
- **Default player in one click**: a card at the top of Settings → General. Pylon takes every format you haven't picked an app for and opens its page in Windows' default apps for the rest, and shows how many formats already open in Pylon.
- **Detailed update download**: size, speed and time left. Assembling the new version from the downloaded changes is shown as its own step instead of looking stuck at 70%, and the download can be cancelled.
- **What's playing in the sidebar** gets a backdrop and bouncing bars over its cover.
- **A scrollbar on the lyrics**: shows on hover and while scrolling, and can be dragged.
- **New lyrics animation styles** (Settings → Interface): Karaoke, a plain fill and lighter; Line by line, whole lines light up and the lightest.
- **The visualizer keeps breathing quietly while paused.**
- **Translations on Weblate**: the interface can be translated in the browser; "Help translate Pylon" is in Settings → General.

**Fixes**
- Lyrics no longer vanish: after the same lyrics were loaded again, every line turned transparent.
- The visualizer in Settings and in the right-click menu is now one setting (Off / Bars / Mirror / Wave / Pulse), and video no longer has one. VLC's own visualization is labelled separately.
- Clearer names for the skip amounts: which jump is on which keys.

## 1.9.3

<!-- ru -->
**Новое**
- **Лёгкий режим** (Настройки → Интерфейс): без размытия и свечения текста, без движущегося фона, анимация спокойнее. Для слабых ноутбуков и экономии батареи.

**Исправления**
- Свёрнутый или убранный в трей Pylon больше ничего не рисует: текст, спектр, полоса перемотки и живой фон останавливаются. Играет только звук.
- Размытый фон считается в десятки раз дешевле при том же виде, спектр обновляется 30 раз в секунду и не создаёт мусор на каждом кадре, а невидимые строки текста не перерисовываются.
- Вместо окна «Что нового» после обновления — короткое сообщение «Pylon обновлён до …», один раз при первом запуске. Список изменений по-прежнему виден в окне «Доступно обновление».

<!-- en -->
**New**
- **Light mode** (Settings → Interface): no blur or glow on lyrics, no moving background, calmer animation. For weak laptops and battery life.

**Fixes**
- Minimized or in the tray, Pylon draws nothing: lyrics, spectrum, seek bar and the moving background stop. Only the sound plays.
- The blurred background costs a fraction of what it did with the same look, the spectrum updates 30 times a second without making garbage every frame, and lyrics lines off screen aren't repainted.
- Instead of the "What's new" window after an update, a short "Pylon updated to …" note, once, on the first start. The changes are still listed in the "Update available" window.

## 1.9.2

<!-- ru -->
**Исправления**
- **Звук без «радиопомех».** Если частота файла не совпадала с частотой звуковой карты (обычные 44,1 кГц на выходе 48 кГц), libVLC пересчитывал её самым грубым способом, без фильтрации, и музыка звучала как радио с плохим сигналом. Теперь используется качественный ресемплер.
- **Выбранное устройство вывода** включается до начала воспроизведения. Раньше переключение на ходу сбивало часы libVLC: он каждые 0,2 секунды выбрасывал куски звука и загружал целое ядро процессора. Смена устройства в настройках теперь продолжает трек с того же места.
- **Меньше нагрузки на процессор.** Текст песни и спектр обновляются 60 раз в секунду, а не с частотой монитора (на 144–240 Гц это было втрое дороже). Полоса перемотки больше не перерисовывает окно на каждом кадре. Всё вместе в 3–5 раз меньше нагрузки во время воспроизведения.
- **Статус в Discord** снова появляется: Pylon ждал ответа, который Discord не присылает, и зависал на подключении.
- **Обложки и тексты** находятся чаще: исполнитель кириллицей теперь совпадает с латиницей в iTunes («Лолита» = «Lolita»), а если iTunes ничего не знает, обложка берётся из Spotify. Файлы без тегов получают исполнителя и название из имени файла («Исполнитель - Название»).
- **Узнавание песни** запускается и для файлов, у которых не нашлась обложка, и не повторяется по кругу.
- **«Что нового»** больше не показывается при каждом запуске, если диск был переполнен: настройки и библиотека записываются через временный файл, и неудачная запись не портит их.
- **yt-dlp**: зависшее скачивание прерывается через 20 секунд с понятной ошибкой. Песня из импорта Spotify, которая не нашлась на YouTube, показывает причину, а не запускает цепочку ожиданий по всему плейлисту.

<!-- en -->
**Fixes**
- **No more "bad radio" sound.** When a file's sample rate differed from the sound card's (the usual 44.1 kHz on a 48 kHz output), libVLC converted it the crudest way, with no filtering. A proper resampler is used now.
- **The chosen output device** is set before playback starts. Switching it on a playing stream threw libVLC's clock off: it dropped audio every 0.2 s and kept a CPU core busy. Changing the device in Settings now carries on from the same spot.
- **Lower CPU use.** Lyrics and the spectrum update 60 times a second instead of at the monitor's rate (three times the work at 144–240 Hz), and the seek bar no longer redraws the window every frame: 3–5× less CPU while playing.
- **Discord status** shows up again: Pylon waited for a reply Discord never sends and hung while connecting.
- **Covers and lyrics** are found more often: a Cyrillic artist now matches iTunes' Latin spelling ("Лолита" = "Lolita"), and when iTunes has nothing the cover comes from Spotify. Untagged files take artist and title from the file name ("Artist - Title").
- **Song recognition** also runs for files whose cover wasn't found, and doesn't repeat over and over.
- **"What's new"** no longer appears at every start after the disk was full: settings and the library are written through a temporary file, so a failed write can't damage them.
- **yt-dlp**: a stalled download stops after 20 s with a clear message, and a Spotify-imported song that isn't on YouTube shows why instead of starting a chain of waits through the whole playlist.

## 1.9.1

<!-- ru -->
**Новое**
- **Раскладки клавиш** (Настройки → Управление → «Раскладка»): как в VLC, как на YouTube (K — пауза, J и L — перемотка) или как в MPC-HC (Alt+Enter, PgUp и PgDn). Ваши собственные сочетания остаются поверх выбранной раскладки.
- **Плеер по умолчанию и типы файлов** теперь легко найти: «⋯» → «Плеер по умолчанию и типы файлов…» и первая страница настроек.
- **Другие языки интерфейса**: перевод — это один JSON-файл в папке lang; Pylon сам находит его и показывает в списке языков. Шаблон со всеми строками: `Pylon.exe --export-strings de.json`.

**Исправления**
- Видно, что выбрано: выбранный вариант в переключателях («Выкл / Слабое / …») — светлая плашка, а включённые кнопки («Повтор», «Перемешать», «Текст» и другие) — с подложкой и точкой снизу, даже с серым акцентом.
- Ползунки эквалайзера, компрессора и пространства меняют звук на лету, без пересборки фильтров: трек больше не прерывается. Короткая пауза остаётся только при включении или выключении самого эффекта.
- Бэк-вокал в текстах подсвечивается по словам, в том числе строки, где поёт только бэк.
- Узнанная песня получает обложку из iTunes, если в базе MusicBrainz её нет.

<!-- en -->
**New**
- **Shortcut layouts** (Settings → Controls → Layout): VLC, YouTube (K pauses, J and L skip) or MPC-HC (Alt+Enter, PgUp and PgDn). Your own shortcuts stay on top of the layout.
- **Default player and file types** are easy to find: ⋯ → Default Player & File Types…, and the first page of Settings.
- **More interface languages**: a translation is one JSON file in the lang folder; Pylon finds it and lists it with the languages. A template with every text: `Pylon.exe --export-strings de.json`.

**Fixes**
- You can see what's selected: the chosen option in segmented switches is a solid chip, and toggle buttons that are on (Repeat, Shuffle, Lyrics and others) get a backdrop and a dot underneath, even with a grey accent.
- Equalizer, compressor and spatializer sliders change the sound as it plays, without rebuilding the filters: the track no longer breaks up. A short gap remains only when an effect itself is switched on or off.
- Background vocals in lyrics light up word by word, including lines sung by the background alone.
- A recognized song gets its cover from iTunes when MusicBrainz has none.

## 1.9.0

<!-- ru -->
**Новое**
- **Spotify.** «⋯» → «Импорт из Spotify» (или ссылка в «Открыть сетевой поток»): плейлист, альбом или трек по ссылке становится плейлистом Pylon. Песни из вашей библиотеки играют с диска, остальные — звуком с YouTube. «⋯» → «Текст для Spotify» — окно с текстом в стиле Spicy Lyrics для того, что сейчас играет в приложении Spotify на этом компьютере; клик по строке перематывает Spotify.
- **Узнавание песни по звуку**, как Shazam: песни без тегов получают название, исполнителя, альбом и обложку (AcoustID + MusicBrainz). Вручную — правый клик → «Узнать песню». Нужно «Загружать названия и обложки из интернета».
- **Субтитры из интернета** (Субтитры → «Найти субтитры в интернете…»): OpenSubtitles, точные совпадения для вашего файла сверху; файл сохраняется рядом с видео.
- **Last.fm**: скробблинг прослушанного, вход через сайт Last.fm (Настройки → Приватность).
- **Статус в Discord**: «Слушает …» / «Смотрит …» в профиле (Настройки → Приватность).
- **Виды визуализации**: столбики, зеркало, волна и «пульс» — мягкие волны, которые дышат с музыкой; выключается и переключается правым кликом → «Визуализация» или в Настройки → Интерфейс.
- **Склейка файлов** (Конвертировать → «Склеить в один файл») через ffmpeg (скачивается при первом использовании, около 200 МБ).
- **Смотреть вместе** («⋯» → «Смотреть вместе»): друзья в той же сети или общем VPN повторяют за вами паузу и позицию.
- **Типы файлов**: окно со всеми значками форматов и кнопкой «Сделать Pylon по умолчанию» (после установки и в Настройки → Библиотека); там же превью в Проводнике для любых форматов через Icaros.
- Плавная прокрутка колесом во всех списках и тонкие полосы прокрутки, которые расширяются под курсором.

**Исправления**
- Поля ввода: текст больше не обрезается и не уезжает от подсказки, поиск в настройках снова виден.
- Эффекты и эквалайзер применяются сразу, без перезапуска движка: больше не дёргаются звук и картинка при каждом движении ползунка.
- Открытие больших папок не подвешивает окно.
- Обновление скачивается с прогрессом прямо в окне «Доступно обновление».
- Переход со старого установщика сохраняет закреплённый в «Пуске» ярлык.
- Настройки: «Дополнительно» показано сразу, без кнопки внизу; нет тёмной полосы сверху и пунктов, которые ничего не делали; ключи для текстов песен — необязательные и убраны в «Дополнительно».
- Spicy Lyrics находит больше песен и перепроверяет ненайденные через неделю.
- Кнопки на панели по умолчанию: добавлены «Повтор», «Перемешать», «Во весь экран» и «Мини-плеер».

<!-- en -->
**New**
- **Spotify.** ⋯ → Import from Spotify (or a link in Open Network Stream): a playlist, album or track link becomes a Pylon playlist. Songs in your library play from disk, the rest as sound from YouTube. ⋯ → Lyrics for Spotify: a window with Spicy-style lyrics for what the Spotify app on this PC is playing; click a line to seek Spotify.
- **Recognize songs by their sound**, like Shazam: songs without tags get title, artist, album and cover (AcoustID + MusicBrainz). By hand: right-click → Recognize Song. Needs "Fetch titles and cover art from the internet".
- **Subtitles from the internet** (Subtitles → Find Subtitles Online…): OpenSubtitles, exact matches for your file first; saved next to the video.
- **Last.fm** scrobbling, signing in on the Last.fm website (Settings → Privacy).
- **Discord status**: "Listening to …" / "Watching …" on your profile (Settings → Privacy).
- **Visualizer styles**: bars, mirror, wave and pulse (soft waves that breathe with the music); switch or turn off with right-click → Visualizer, or in Settings → Interface.
- **Join files** (Convert → Join into one file) through ffmpeg (fetched on first use, about 200 MB).
- **Watch together** (⋯ → Watch Together): friends on the same network or a shared VPN follow your play, pause and position.
- **File types**: a window with every format's icon and "Make Pylon the default" (after installing and in Settings → Library), plus Explorer previews for any format through Icaros.
- Smooth wheel scrolling in every list, and slim scrollbars that widen under the pointer.

**Fixes**
- Text fields: text is no longer clipped or shifted off the placeholder, and the settings search shows what you type.
- Effects and the equalizer apply at once, without restarting the engine: no more stutter on every slider move.
- Opening big folders no longer freezes the window.
- Updates download with a progress bar right in the "Update available" window.
- Moving over from the old installer keeps your Start menu pin working.
- Settings: Advanced options show right away instead of behind a button; no dark band at the top or options that did nothing; lyrics keys are optional and moved to Advanced.
- Spicy Lyrics finds more songs and checks songs it missed again after a week.
- Default control bar: Repeat, Shuffle, Fullscreen and Miniplayer added.

## 1.8.0

<!-- ru -->
**Новое**
- **Пульт с телефона** (меню «⋯» → «Пульт с телефона…»): отсканируйте QR-код телефоном в той же сети — в браузере откроется пульт с паузой, треками, перемоткой на 15 секунд, громкостью и тем, что играет. В адресе секретный ключ: чужие в сети управлять не смогут. Ничего ставить на телефон не нужно.

**Под капотом**
- Каждая сборка проверяет то, что раньше ломалось незаметно: разбор текстов песен с переводами, включение аудиоэффектов и громкость каждого плеера (для плавного перехода и выравнивания).

<!-- en -->
**New**
- **Phone remote** (⋯ menu → Phone Remote…): scan the QR code with a phone on the same network and its browser becomes a remote with play / pause, tracks, 15-second skips, volume and what's playing. The address carries a private key, so others on the network can't take over. Nothing to install on the phone.

**Under the hood**
- Every build now checks what used to break silently: lyrics parsing with translations, audio effects engaging, and per-player loudness (crossfade, measured replay gain).

## 1.7.0

<!-- ru -->
**Видео**
- **«Пропустить заставку»**: кнопка появляется во время главы Intro / Opening, а если глав нет — до отметки, поставленной один раз на всю папку (правый клик → Воспроизведение → «Здесь кончается заставка»). Можно пропускать автоматически.
- **«Следующая серия»**: за 30 секунд до конца или на титрах — кнопка со следующим файлом из очереди или папки, в порядке Проводника («Серия 2» перед «Серия 10»). После видео следующая серия включается сама; выключается в Настройки → Воспроизведение.
- **Лист кадров** (правый клик → Видео → «Лист кадров…»): 16 кадров с метками времени, именем, длительностью и размером в одной PNG, в папке снимков.
- Миниатюры в библиотеке для видео, которые Windows не умеет показывать (FLV, RMVB, VOB и т. п.): кадр берётся через libVLC.
- Безымянные звуковые дорожки подписаны кодеком, каналами, частотой и языком: «Дорожка 2 · AAC · стерео · 48 kHz».

**Тексты песен**
- **Spicy Lyrics работает у всех без настройки**: в Pylon встроен свой публичный ключ. Свой ключ можно указать по желанию.

**Исправления**
- Превью на шкале перемотки больше не может зависнуть в фоне при смене файла (взаимная блокировка внутри libVLC).

<!-- en -->
**Video**
- **Skip Intro**: a button during a chapter named Intro / Opening, or, without chapters, up to a mark set once per folder (right-click → Playback → Intro Ends Here). Can skip automatically.
- **Next Episode**: 30 seconds before the end or during the credits, a button with the next file from the queue or the folder, in Explorer's order ("Episode 2" before "Episode 10"). After a video the next episode plays by itself; turn off in Settings → Playback.
- **Contact sheet** (right-click → Video → Contact Sheet…): 16 frames with times, name, length and size in one PNG, in the snapshot folder.
- Library thumbnails for videos Windows can't preview (FLV, RMVB, VOB and the like): a frame through libVLC.
- Unnamed audio tracks are labeled with codec, channels, rate and language: "Track 2 · AAC · stereo · 48 kHz".

**Lyrics**
- **Spicy Lyrics works for everyone with no setup**: Pylon has its own public key. Your own key is optional.

**Fixes**
- The seek bar preview can no longer hang in the background when the file changes (a lock-up inside libVLC).

## 1.6.0

<!-- ru -->
**Музыка**
- **Перевод текста песни** мелкой строкой под оригиналом — если он есть в файле (TTML из Apple Music с переводами, двуязычный LRC). Выключается в Настройки → Интерфейс → Текст песни.
- **Меню «Текст песни»** (правый клик в режиме музыки): найти текст под другим названием (Spicy Lyrics, затем LRCLIB), открыть свой файл .lrc / .ttml, сдвинуть синхронизацию раньше или позже на 0,25 с. Сдвиг запоминается для каждого трека.
- **Обложки из интернета** для песен без своей: ищутся в iTunes по исполнителю и альбому, видны в окне и в медиа-панели Windows. Работает при включённом «Загружать названия и обложки из интернета».
- **Умные плейлисты**: «Часто слушаю», «Недавно слушал», «Недавно добавлено».
- **«Играть следующим»** в меню любых файлов и папок; в «Сейчас играет» строки перетаскиваются мышью.
- **Одинаковая громкость без тегов**: «Replay Gain → Измеренная» (Настройки → Аудио). Pylon сам измеряет каждую песню (заодно с волной на шкале), а следующую в очереди — заранее, поэтому она сразу звучит на своём уровне. Плавный переход это учитывает.
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
