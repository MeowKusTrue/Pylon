# Changelog

Each version's section becomes its GitHub release notes (`.github/workflows/release.yml`), and Pylon shows it in
"What's new" after updating — the part between `<!-- ru -->` and `<!-- en -->` in Russian, the rest in English.

## 2.0.1

<!-- ru -->
**Новое**
- **Умный переход между песнями.** Переход начинается там, где песня затихает, а не в самом конце файла, а следующая песня начинается сразу с музыки, без тишины в начале. Раньше при песне с тихим хвостом между треками бывало почти 3 секунды тишины, теперь их нет.
- **Внутри одного альбома без перехода.** Песни одного альбома идут друг за другом, как задумано (концертные и сведённые альбомы не смешиваются). Включается отдельно: Настройки → Воспроизведение → «Переход внутри альбома».
- **Мягкая пауза.** Музыка на мгновение плавно затихает и нарастает при паузе, продолжении и переключении песни, а не обрывается на полузвуке. Можно выключить там же.

**Исправления**
- **Видео 60 кадров/с больше не замедляется после перемоток.** H.264 с B-кадрами (так сжато большинство скачанных видео) после второй перемотки начинал показывать около 30 кадров в секунду вместо 60 и так и оставался. Теперь 60, а после перемотки картинка идёт через 0,05–0,2 с. Записи OBS в H.264 тоже: 0,16 с вместо 1–2 с.
- **Перемотка в TS, M2TS, MPG и VOB без замирания картинки.** В этих форматах нет списка ключевых кадров, и после перемотки картинка ждала следующий (до 2 с). Теперь Pylon сам находит ближайший ключевой кадр и перематывает на него, как MPC-HC: картинка через 0,16–0,2 с (MPG/VOB — до 0,5 с). Касается записей с ТВ-тюнеров, IPTV, камер (AVCHD) и DVD.

<!-- en -->
**New**
- **Smart crossfade.** The crossfade starts where the song goes quiet, not at the very end of the file, and the next song starts right with the music, past its silence. A song with a quiet tail used to leave almost 3 seconds of silence between tracks; now there is none.
- **No crossfade within an album.** Songs of one album follow each other as intended (live and mixed albums aren't blended). It can be turned on in Settings → Playback → "Crossfade within an album".
- **Soft pause.** Music fades out and in for a moment on pause, resume and skip instead of stopping mid-sound. It can be turned off there too.

**Fixes**
- **60 fps video no longer slows down after seeking.** H.264 with B-frames (how most downloaded videos are encoded) dropped to about 30 frames a second after the second seek and stayed there. Now it plays at 60, and after a seek the picture carries on in 0.05–0.2 s. OBS recordings in H.264 too: 0.16 s instead of 1–2 s.
- **Seeking in TS, M2TS, MPG and VOB without a frozen picture.** These formats have no list of keyframes, so after a seek the picture waited for the next one (up to 2 s). Now Pylon finds the nearest keyframe itself and seeks to it, as MPC-HC does: the picture carries on in 0.16–0.2 s (MPG/VOB up to 0.5 s). For TV tuner and IPTV recordings, camcorders (AVCHD) and DVDs.

## 2.0.0

<!-- ru -->
**Новое**
- **Вторые субтитры.** Сверху картинки можно показать ещё одни субтитры, например перевод рядом с оригиналом, чтобы учить язык. Подходит файл рядом с видео, любой выбранный файл или дорожка внутри видео. Субтитры → Вторые субтитры.
- **Автосинхронизация субтитров.** Если субтитры отстают или спешат, Pylon сам находит, где в видео говорят, и сдвигает их. Исправляет и разную частоту кадров (23,976 ↔ 25). Исправленная копия сохраняется рядом с оригиналом (`.synced.srt`). Субтитры → Синхронизировать субтитры автоматически.
- **Субтитры из речи.** Если субтитров нет совсем, Pylon делает их из речи в видео через whisper.cpp, на вашем компьютере и без интернета после первой загрузки. На видеокартах NVIDIA работает быстрее. Можно сразу перевести на английский. Субтитры → Субтитры из речи…
- **Редактор тегов.** Название, исполнитель, альбом, год, номер, жанр и обложка (с компьютера или найденная в интернете). Правой кнопкой по песне → Изменить теги…
- **Предыдущий кадр:** клавиша `,` (в раскладке MPC-HC `Ctrl+←`).
- **Super Resolution и HDR из SDR** включаются прямо из меню Видео, без похода в настройки.
- **Сообщить о проблеме.** Меню ⋯ → Сообщить о проблеме…: Pylon собирает архив с версиями, видеокартой и журналами ошибок (без названий файлов, паролей и ключей) и открывает страницу GitHub, куда его приложить.
- **Совместный просмотр без Pylon.** На странице приглашения появились кнопки «Смотреть в браузере» (прямо на странице, ничего ставить не надо) и «Скачать Pylon».

**Исправления**
- **Картинка больше не замирает после перемотки.** У записей OBS и других «фрагментированных» MP4 звук после перемотки шёл сразу, а картинка стояла 1–2 секунды. Теперь картинка продолжается почти мгновенно (~0,1 с), как в MPC-HC.
- **Видео открывается быстрее** примерно на 0,6–0,9 с, когда Pylon запускают двойным щелчком по файлу: видеодрайвер готовится, пока строится окно, и файл начинает открываться, не дожидаясь, пока окно нарисуется.

<!-- en -->
**New**
- **A second subtitle.** Show another subtitle at the top of the picture, such as a translation next to the original for learning a language. It can be a file beside the video, any file you choose, or a track inside the video. Subtitles → Second Subtitle.
- **Automatic subtitle sync.** When subtitles run early or late, Pylon finds where people speak in the video and shifts them to match. It also fixes a frame-rate mix-up (23.976 ↔ 25). The fixed copy is saved beside the original (`.synced.srt`). Subtitles → Sync Subtitles Automatically.
- **Subtitles from speech.** When a video has no subtitles at all, Pylon makes them from its speech with whisper.cpp, on your computer and offline after the first download. Faster on NVIDIA cards. It can translate into English as well. Subtitles → Subtitles from Speech…
- **Tag editor.** Title, artist, album, year, track number, genre and cover (from your computer or found online). Right-click a song → Edit Tags…
- **Previous frame:** the `,` key (`Ctrl+←` in the MPC-HC layout).
- **Super Resolution and HDR from SDR** can be switched on right from the Video menu.
- **Report a problem.** ⋯ menu → Report a Problem…: Pylon packs a zip with versions, graphics card and error logs (no file names, passwords or keys) and opens the GitHub page to attach it to.
- **Watch Together without Pylon.** The invite page has "Watch in the browser" (right on the page, nothing to install) and "Download Pylon" buttons.

**Fixes**
- **The picture no longer freezes after a seek.** With recordings from OBS and other "fragmented" MP4s, the sound carried on right after a seek but the picture stood still for 1–2 seconds. Now it carries on almost at once (~0.1 s), as in MPC-HC.
- **Videos open faster,** by about 0.6–0.9 s when Pylon is started by double-clicking a file: the graphics driver gets ready while the window is built, and the file starts opening without waiting for the window to be drawn.

## 1.9.14

<!-- ru -->
**Исправления**
- **Вход в YouTube через браузер.** Если YouTube не принял выбранный вход (устаревший или из профиля, где вы не вошли), Pylon больше не повторяет одну и ту же ошибку: он говорит, что вход не сработал, и предлагает выбрать другой профиль. Рядом с каждым профилем видно, когда им пользовались в последний раз («сегодня», «47 дн. назад»). Наверху стоит тот, что используется сейчас, остальные идут по свежести.

<!-- en -->
**Fixes**
- **Signing in to YouTube through your browser.** When YouTube turns down the sign-in you chose (out of date, or from a profile that isn't signed in), Pylon no longer repeats the same error: it says that sign-in didn't work and offers to pick another profile. Each profile shows when it was last used ("used today", "used 47 days ago"). The one in use comes first, the rest by freshness.

## 1.9.13

<!-- ru -->
**Новое**
- **Вход в YouTube через браузер.** Когда YouTube просит войти (видео 18+ или «подтвердите, что вы не бот»), Pylon спрашивает, откуда взять вход: из основного браузера, из другого установленного, из нужного профиля или из файла cookies.txt. Потом песня включается снова. Профиль, которым пользуется основной Firefox, стоит в списке первым. Если браузер не отдал свой вход (Chrome или Edge открыты либо заблокированы), Pylon объясняет, что сделать. Выбор меняется в Настройки → Сеть.
- **Визуализатор под обложкой окрашивается в цвет обложки,** как полноэкранный. Без обложки остаётся цвет темы.

**Исправления**
- Полоска перемотки снова в цвете темы, как раньше. В 1.9.12 по ошибке в цвет обложки перекрасилась она, а не визуализатор.

<!-- en -->
**New**
- **Sign in to YouTube through your browser.** When YouTube wants you signed in (age-restricted videos, or "confirm you're not a bot"), Pylon asks where to take the sign-in from: your main browser, another installed one, a particular profile, or a cookies.txt file. Then the song plays again. The profile your main Firefox uses comes first in the list. When a browser won't give up its sign-in (Chrome or Edge open or locked), Pylon says what to do. The choice can be changed in Settings → Network.
- **The visualizer under the cover takes the cover's color,** like the full-screen one. Without a cover it keeps the theme's color.

**Fixes**
- The seek bar is back in the theme's color. In 1.9.12 it was recolored by mistake instead of the visualizer.

## 1.9.12

<!-- ru -->
**Новое**
- Полоска перемотки у музыки окрашивается в цвет обложки, как анимация на фоне. Без обложки остаётся цвет темы.

**Исправления**
- Обложки песен из интернета (Spotify, YouTube, Deezer) не показывались в самом плеере, хотя были в списке и в Discord. Теперь показываются, а обложка, найденная уже во время песни, появляется сразу.
- Если YouTube иногда отклоняет поток (ошибка 403), Pylon один раз получает новый адрес и продолжает с того же места, вместо того чтобы пропускать песню.

<!-- en -->
**New**
- The seek bar takes the cover's color for music, like the animation behind it. Without a cover it keeps the theme's color.

**Fixes**
- Covers of songs from the web (Spotify, YouTube, Deezer) didn't show in the player itself, though the list and Discord had them. They show now, and a cover found while the song plays appears at once.
- When YouTube now and then turns a stream down (error 403), Pylon fetches a fresh address once and carries on from the same place instead of skipping the song.

## 1.9.11

<!-- ru -->
**Исправления**
- **Песни из плейлистов снова играют как музыка.** В 1.9.9 песни из импортированных плейлистов (а ещё радио и поиска) при включённом видео открывались как клипы: без текста, без музыкального экрана, не скробблились и попадали в «Продолжить просмотр». Теперь песня — это всегда песня: только звук, текст, обложка. Ссылки music.youtube.com тоже открываются как песни. История, записанная с этой ошибкой, исправляется сама.
- **Если оригинальный релиз не нашёлся,** Pylon сначала ищет у того же исполнителя другую запись этой песни (альбомную, ремастер) и только потом обычное видео. Если найденная запись длиннее или короче оригинала, Pylon подсказывает, как подвинуть текст. В меню «Текст песни» появляется пункт «Текст позже на N с» с разницей во времени, а синхронизацию теперь можно двигать клавишами Shift+G и Shift+H.
- Песня, которой нет на YouTube, больше не останавливает плейлист: Pylon переходит к следующей. После трёх неудач подряд он останавливается, чтобы не пролистать весь список, если YouTube недоступен.
- Текст для песни из плейлиста ищется по длине оригинала, а не найденной записи: клип со вступлением больше не путает поиск, а заранее найденный текст подхватывается сразу.

<!-- en -->
**Fixes**
- **Songs from playlists play as music again.** In 1.9.9, songs from imported playlists (and from the radio and the search) opened as clips when video was on: no lyrics, no music view, no scrobbles, and they landed in Continue Watching. A song is now always a song: its sound only, with lyrics and cover. music.youtube.com links open as songs too. History written down during the bug repairs itself.
- **When the original release isn't found,** Pylon first looks for another recording of the song by the same artist (the album version, a remaster), and only then for a plain video. When the recording runs longer or shorter than the original, Pylon says how to shift the lyrics. The Lyrics menu gets a "Lyrics Later by N s" item with the difference, and the timing can now be moved with Shift+G and Shift+H.
- A song that isn't on YouTube no longer stops the playlist: Pylon moves on to the next one. After three misses in a row it stops, so it doesn't skip through the whole list while YouTube is unreachable.
- Lyrics for a playlist song are looked up by the original's length, not the found recording's: a clip with an intro no longer throws the search off, and lyrics found ahead are used at once.

## 1.9.10

<!-- ru -->
**Новое**
- **Плейлисты офлайн.** Правый клик по плейлисту → «Скачать для офлайна»: песни из интернета сохраняются в папку (по умолчанию «Музыка\Pylon», меняется в Настройки → Библиотека) и дальше играют сразу, без сети и прокси, с теми же названиями, обложками и текстами. Новые песни, которые придут при обновлении плейлиста из Spotify, тоже скачиваются.
- **YouTube Music в поиске.** Поиск по библиотеке показывает под вашими файлами песни из YouTube Music с обложками: щелчок включает песню, а правый клик ставит её следующей, в очередь или запускает по ней радио. Отключается в Настройки → Библиотека.
- **Обложка в Discord.** В статусе «Слушает» видна обложка песни: для песен из Spotify и YouTube — их собственная, для своих файлов — обложка альбома с Deezer.
- **Лайки умнее.** «Нравится» ставит ♥ в Last.fm, а радио после лайков подбирает каждую вторую порцию песен по тому, что вам понравилось.
- **Умные плейлисты учитывают песни из Spotify и радио**, а не только файлы на диске. Добавлен «Давно не слушал»: песни, которые вы слушали часто, но не последний месяц.

**Исправления**
- У плейлистов, импортированных из Spotify раньше, в боковой панели не было обложек. Теперь Pylon сам подтягивает недостающие обложки песен, а плейлист без своей обложки показывает обложку первой песни.

**Быстрее**
- Библиотека больше не переписывается целиком на диск при каждом изменении (сыгранная песня, найденная обложка, порция радио). Изменения собираются и записываются раз в полторы секунды в фоне и при выходе. С большой папкой музыки окно больше не подтормаживает.
- Песни из Spotify и радио сопоставляются с вашими файлами через указатель по названиям, а не перебором всей библиотеки.
- Кэш миниатюр больше не растёт без предела.

<!-- en -->
**New**
- **Offline playlists.** Right-click a playlist → Download for Offline: its songs from the web are saved into a folder (Music\Pylon by default, changeable in Settings → Library) and then play at once, without a connection or a proxy, with the same names, covers and lyrics. Songs a Spotify update adds later come down too.
- **YouTube Music in search.** The library search lists songs from YouTube Music, with covers, under your own files: a click plays one, a right-click plays it next, queues it or starts a radio from it. It can be turned off in Settings → Library.
- **Cover art in Discord.** "Listening to" shows the song's cover: their own for songs from Spotify and YouTube, the album cover from Deezer for your files.
- **Smarter likes.** Like gives the song a ♥ on Last.fm, and after a like the radio picks every other batch from what you liked.
- **Smart playlists count songs from Spotify and the radio**, not only files on disk. New: Forgotten Favorites, songs you played often but not in the last month.

**Fixes**
- Playlists imported from Spotify with earlier versions had no covers in the sidebar. Pylon now fetches the missing song covers itself, and a playlist without a cover of its own shows its first song's.

**Faster**
- The library is no longer rewritten to disk whole on every change (a song played, a cover found, a radio batch). Changes are gathered and written every second and a half in the background, and on exit. With a big music folder the window no longer stutters.
- Songs from Spotify and the radio find your files through an index by name instead of a pass over the whole library.
- The thumbnail cache no longer grows without limit.

## 1.9.9

<!-- ru -->
**Новое**
- **Радио по песне.** Правый клик по песне → «Радио по этой песне» (или клавиша W): дальше бесконечно играют похожие песни из YouTube Music, а то, что есть у вас в библиотеке, играет из библиотеки. «Нравится» (Y) добавляет песню в «Любимые песни» и ставит следующими похожие на неё, «Не нравится» (Shift+Y) пропускает её, и радио её больше не предложит.
- **Девять новых пословных анимаций текста:** «Цветок», «Блеск», «Пружина», «Каскад», «Фокус», «Сияние», «Машинка», «Качели» и «Круги». У каждой анимации свой шрифт (любой из установленных в Windows), а спетые слова можно красить в цвет обложки: яркость подстраивается так, чтобы текст оставался читаемым и на тёмных, и на бледных обложках.
- **Текст ищется заранее** для каждой песни, даже при закрытой панели, а для следующей — пока играет текущая. В миниплеере текст теперь загорается по словам, пульт для других плееров тоже показывает новые анимации.
- **Spotify:** вход работает сразу, без своего приложения (своё можно указать по желанию), одна кнопка «Войти / Выйти» для Spotify и для Last.fm. После входа плейлист импортируется целиком, с обложкой. Без входа импортируются первые 50 песен, а обложки и коды песен подтягиваются с Deezer.
- **Импортированные плейлисты Spotify обновляются сами:** при запуске и раз в 6 часов.
- **Песни из Spotify ищутся в YouTube Music по коду записи (ISRC):** играет официальный релиз, а не клип, кавер или лирик-видео. Поиск идёт быстрее, а следующие две песни готовятся заранее, поэтому переключение занимает меньше секунды.

**Исправления**
- **Шумы в звуке.** После перемотки DirectSound на мгновение сообщал задержку на 40 мс неверно, и libVLC около 9 секунд «подгонял» звук грубым ресемплером: это звучало как плохое радио. Подгонка отключена, звук после перемотки остаётся чистым. Замерено записью через виртуальный кабель: было 2–7 дБ сигнал/шум после каждой перемотки, стало 68 дБ всё время.

<!-- en -->
**New**
- **Radio by a song.** Right-click a song → Radio by This Song (or press W): similar songs from YouTube Music play on and on, and the ones in your library play from the library. Like (Y) adds the song to Liked songs and puts more like it next; Dislike (Shift+Y) skips it, and the radio never offers it again.
- **Nine new word-by-word lyric animations:** Bloom, Shimmer, Bounce, Cascade, Focus, Aurora, Typewriter, Swing and Ripple. Each animation has its own font (any font installed in Windows), and sung words can take the cover's color, with its brightness adjusted so the text stays readable on dark and pale covers alike.
- **Lyrics are looked up ahead** for every song, even with the panel closed, and for the next song while the current one plays. The miniplayer lights the lyrics word by word, and the remote for other players shows the new animations too.
- **Spotify:** sign-in works out of the box, no app of your own needed (you can still use yours), with one Sign in / Sign out button for Spotify and for Last.fm. Signed in, a playlist comes in whole, with its cover. Without signing in, the first 50 songs come in, with covers and song codes from Deezer.
- **Imported Spotify playlists keep themselves up to date:** at start and every 6 hours.
- **Spotify songs are found on YouTube Music by their recording code (ISRC):** the official release plays, not a clip, cover or lyric video. Finding them is faster, and the next two songs are prepared ahead, so switching takes under a second.

**Fixes**
- **Noise in the sound.** Right after a seek, DirectSound briefly reported its delay 40 ms off, and libVLC spent about 9 seconds "correcting" with a crude resampler that sounded like a bad radio. The correction is off, and the sound stays clean after seeking. Measured on a virtual-cable recording: 2–7 dB signal-to-noise after every seek before, 68 dB throughout now.

## 1.9.8

<!-- ru -->
**Новое**
- **Смотреть вместе — как кооператив на одном экране.** Одна шкала на всех: пауза любого ставит паузу всем, перемотка любого двигает всех. Все видят, кто что сделал, а пока кто-то держит паузу, на экране висит «Аня поставила на паузу — ждём». Имя задаётся в Настройки → Онлайн.
- **Пульт для других плееров можно открыть в любой момент:** «⋯» → «Управлять другим плеером», правый клик или Настройки → Онлайн. У пульта есть кнопка закрытия.
- **Свой прокси для сайтов:** любой формат (http, https, socks4, socks5, socks5h, с логином и паролем) и кнопка «Проверить», которая показывает, открывается ли YouTube и работает ли yt-dlp.
- **Настройки разложены по смыслу:** «Внешний вид» (тема, фон, визуализаторы, окно), «Тексты песен», «Онлайн» (пульт, Spotify, Смотреть вместе, Discord, Last.fm) и так далее. Фон музыки и видео меняется ещё и правым кликом.

**Исправления**
- Песни из плейлистов Spotify снова играют: они находились на YouTube, но через прокси или VPN YouTube отдавал ошибку 403. Теперь поток идёт через тот же прокси.
- Перемотка видео больше не отстаёт от ползунка, а удержание стрелки складывает прыжки.
- Быстрое переключение файлов больше не подвешивает окно.
- Переключатели в настройках больше не дёргаются при перерисовке страницы (режим разработчика, смена темы).
- «Смотреть вместе» больше не показывает себя как включённый пульт телефона.
- Если что-то пошло не так, Pylon не закрывается: ошибка записывается в errors.log, а на экране появляется короткое сообщение.

<!-- en -->
**New**
- **Watch Together plays like split-screen co-op.** One timeline for everyone: anyone's pause pauses all, anyone's seek moves all. Everyone sees who did what, and while someone holds the pause, "Ann paused — waiting for them" stays on screen. Set your name in Settings → Online.
- **The remote for other players opens any time:** ⋯ → Control Another Player, right-click, or Settings → Online. It has a close button.
- **Your own proxy for websites:** any format (http, https, socks4, socks5, socks5h, with a user and password), and a Check button that tells whether YouTube opens and yt-dlp works through it.
- **Settings grouped the way you look for things:** Look & Feel (theme, backgrounds, visualizers, window), Lyrics, Online (the remote, Spotify, Watch Together, Discord, Last.fm) and so on. The music and video background can also be changed with a right-click.

**Fixes**
- Songs from Spotify playlists play again: YouTube had them, but through a proxy or VPN it answered 403. The stream now goes through the same proxy.
- Seeking a video no longer lags behind the slider, and holding an arrow key adds the jumps up.
- Switching files quickly no longer freezes the window.
- Switches in Settings no longer twitch when the page is drawn again (developer mode, theme change).
- Watch Together no longer shows up as the phone remote being on.
- When something goes wrong, Pylon keeps running: the error goes to errors.log with a short note on screen.

## 1.9.7

<!-- ru -->
**Новое**
- **Новые полноэкранные визуализации:** «Био» — живые перетекающие волны вокруг обложки, «Волна» — тонкие кольца гнутся под музыку (по мотивам «Моей волны»), «Цветок» — вращающиеся лепестки, «Круги» — каждый удар баса пускает кольцо. В настройках появилась частота кадров визуализатора: 30, 60 или 120 (в лёгком режиме всегда 30).
- **Пословные стили текста песни:** «Прыжок», «Волна», «Проявление» и «Неон».
- **Пульт для других плееров.** Пока Pylon ничего не играет, а Spotify, Яндекс Музыка или браузер играют, Pylon показывает их песню, обложку и текст с кнопками, перемоткой, перемешиванием и повтором. После входа в Spotify видна и очередь.
- **Смотреть вместе — без файла и откуда угодно.** У кого нет того же файла, тот смотрит поток хоста. Через сервер Pylon друзья подключаются из любой сети по ссылке, а в профиле Discord появляется кнопка «Смотреть вместе».
- **«Добавить в музыку»** в контекстном меню Проводника (подменю с вашими музыкальными папками), в меню трека и в библиотеке. В разделе «Музыка» есть поиск музыки по всему компьютеру.
- **Фон:** у музыки на выбор размытая обложка, цвет обложки или цвет темы, у видео добавлен цвет темы.

**Исправления**
- Текст песни больше не пропадает после открытия боковой панели или смены ширины колонки. Перестройка под новую ширину делается один раз, а не на каждом кадре анимации панели.
- Обложка, вшитая в файл, показывается, даже если libVLC не успел её прочитать.
- Повреждённый кэш текстов больше не закрывает плеер.

<!-- en -->
**New**
- **New full-screen visualizers:** Bio (living, overlapping waves round the cover), Wave (thin rings that bend with the music, after Yandex Music's "My Wave"), Bloom (turning petals) and Ripple (every bass hit sends a ring out). A visualizer frame-rate setting: 30, 60 or 120 (always 30 in light mode).
- **Word-by-word lyric styles:** Jump, Wave, Rise and Neon.
- **A remote for other players.** While Pylon plays nothing and Spotify, Yandex Music or a browser does, Pylon shows their song, cover and lyrics with play / pause, skipping, seeking, shuffle and repeat. Signed in to Spotify, you see its queue too.
- **Watch Together without the file, from anywhere.** Friends without the same file watch the host's stream. Through Pylon's server they join from any network with a link, and your Discord profile gets a "Watch together" button.
- **Add to Music** in Explorer's right-click menu (a submenu with your music folders), in the track menu and in the library. The Music section can search the whole computer for music.
- **Background:** music can sit on the blurred cover, the cover's color or the theme's color; video gets the theme's color too.

**Fixes**
- Lyrics no longer vanish after opening the side panel or resizing the column. They are rebuilt for the new width once, not on every frame of the panel's slide.
- Cover art embedded in a file shows even when libVLC didn't get to it in time.
- A damaged lyrics cache no longer closes the player.

## 1.9.6

<!-- ru -->
**Исправления**
- **Звук больше не рвётся после перемотки.** Пересчёт частоты внутри libVLC (speex) после перемотки раскачивал синхронизацию: каждые 0,2 секунды выбрасывались куски звука, а процессор был загружен. Теперь звук идёт через DirectSound на частоте файла, и пересчитывает её сама Windows. По замерам — 2 сбоя вместо 2950 за семь перемоток.
- **Плеер по умолчанию — правда одной кнопкой**, как в 7-Zip: Pylon сам становится программой для всех своих аудио- и видеоформатов, не нужно выбирать каждый формат в настройках Windows. Если Windows какой-то формат защищает, откроется её страница только для него. При удалении Pylon эти привязки убираются.
- Для музыки libVLC больше не пытается подцепить лежащие рядом файлы (.ttml, .lrc) как субтитры.
- Настройка «Качество пересчёта» убрана: пересчитывает Windows.

<!-- en -->
**Fixes**
- **Sound no longer breaks up after a seek.** libVLC's own sample-rate conversion (speex) threw its sync correction into a spiral after seeking: audio dropped every 0.2 s and a core stayed busy. Sound now goes out through DirectSound at the file's own rate and Windows converts it. Measured: 2 sync errors instead of 2950 over seven seeks.
- **Default player really is one button now**, like 7-Zip: Pylon becomes the app for all its audio and video formats without picking each one in Windows' settings. If Windows protects a format, its settings page opens just for that. Uninstalling Pylon removes these links.
- For music, libVLC no longer tries the .ttml and .lrc files beside it as subtitles.
- The "Resampling quality" setting is gone: Windows does the conversion.

## 1.9.5

<!-- ru -->
**Новое**
- **Полноэкранная визуализация** за обложкой, в цветах обложки: «Круг» — лучи вокруг обложки, «Сияние» — мягкие волнистые ленты, «Горизонт» — широкие столбики с отражением. На паузе она тихо «дышит». Включается в Настройки → Интерфейс или правым кликом → «Полноэкранная визуализация». Работает экономно: 30 кадров в секунду, а в свёрнутом окне не рисует ничего.
- Встроенная визуализация VLC (Goom, «Осциллограф» и прочие) убрана: её заменила новая.

**Исправления**
- Полоса спектра и полноэкранная визуализация используют один и тот же анализ звука на кадр.
- Подпись под обложкой читается поверх визуализации.

<!-- en -->
**New**
- **Full-screen visualizer** behind the cover, in the cover's colors: Radial (rays round the cover), Aurora (soft ribbons) and Horizon (wide bars with a reflection). It breathes quietly while paused. Turn it on in Settings → Interface or right-click → Full-screen Visualizer. Kept light: 30 frames a second, nothing while minimized.
- VLC's own visualizations (Goom, scope and the rest) are gone: the new one replaces them.

**Fixes**
- The spectrum strip and the full-screen visualizer share one sound analysis per frame.
- The caption under the cover stays readable over the visualizer.

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
