# LavPulse

[English](README.md) | Русский

LavPulse — локальный MP3-проигрыватель с двухдековым DJ-микшером на .NET 10 и .NET MAUI.

Приложение декодирует треки в PCM-кэш с произвольным доступом, сводит их в общий стереопоток 48 кГц, применяет мастер-эквалайзер и отображает волны и спектр итогового сигнала. Windows, Android и iOS используют платформенные аудиовыходы и системные медиакнопки; микшер и логика очереди общие.

## Содержание

- [Сборка и запуск](#сборка-и-запуск)
- [Возможности](#возможности)
- [Архитектура](#архитектура)
- [Навигация по репозиторию](#навигация-по-репозиторию)
- [Основные потоки выполнения](#основные-потоки-выполнения)
- [Публичный API](#публичный-api)
- [Жизненный цикл и потоки](#жизненный-цикл-и-потоки)
- [Проверки](#проверки)
- [Ограничения](#ограничения)
- [Точки расширения](#точки-расширения)
- [Зависимости и лицензия](#зависимости-и-лицензия)

## Сборка и запуск

Открой [`Laveyana.Audio.Maui.slnx`](Laveyana.Audio.Maui.slnx) в Visual Studio с установленными **.NET 10 SDK** и workload **.NET MAUI**. Для Android нужны Android SDK и JDK 21. Для Windows нужны инструменты WinUI/MAUI, для iOS — Mac, совместимый Xcode и нагрузка `maui-ios`.

| Цель | Проект | Команда |
|---|---|---|
| Windows | `Laveyana.Audio.Maui.csproj` | `dotnet build samples/Laveyana.Audio.Maui/Laveyana.Audio.Maui.csproj -c Debug` |
| Android | `Laveyana.Audio.Maui.Android.csproj` | `dotnet build samples/Laveyana.Audio.Maui/Laveyana.Audio.Maui.Android.csproj -c Debug -p:AndroidPackageFormats=apk` |

На Mac собери версию для симулятора iPhone следующими командами. Версии SDK и Xcode для воспроизводимой сборки указаны в [проверке iOS](.github/workflows/ios.yml). Для физического устройства выбери `ios-arm64` и настрой подпись команды разработчика Apple.

```bash
dotnet workload install maui-ios
dotnet build samples/Laveyana.Audio.Maui/Laveyana.Audio.Maui.csproj -c Debug -p:LavPulsePlatform=ios -p:RuntimeIdentifier=iossimulator-arm64 -p:EnableCodeSigning=false
```

Перед установкой в симулятор через `simctl install` подпиши созданный пакет командой `codesign --force --deep --sign - path/to/Laveyana.Audio.Maui.app`.

После смены платформы или обновления workload выполни `dotnet restore Laveyana.Audio.Maui.slnx`. Решение разделяет промежуточные каталоги Windows и Android (см. [`Directory.Build.props`](Directory.Build.props)), чтобы не смешивать NuGet-ресурсы и не получать слишком длинные пути Android.

В приложении импортируй MP3-файлы или папку через **Настройки → Медиатека**; в Windows также работает перетаскивание файлов. Выбор трека в очереди запускает переход без изменения порядка списка. Повторное нажатие на активную вкладку **Очередь** скрывает или показывает прослушанные записи.

## Возможности

- Две деки с ручным или автоматическим гиперболическим кроссфейдом, плавным стартом/паузой, повтором одного трека и изменяемой скоростью.
- Десятиполосный мастер-эквалайзер и общее усиление, применяемые **до** расчёта мастер-спектра.
- Детальная и обзорная волны, перемотка жестами, частотный спектр и анимации смены дек.
- Переставляемая очередь, история воспроизведения, ID3-обложки и теги, последовательная фоновая подготовка добавленных MP3.
- Фоновое воспроизведение и системные медиакоманды на Android, Windows и iOS.

## Архитектура

UI не декодирует MP3 в аудиопотоке. Подготовленный трек содержит PCM-кэш и данные волны. Одна аудиосессия сводит деки, применяет мастер-эквалайзер и передаёт позицию фактически воспроизводимого сигнала.

```text
TrackFile -> TrackPreparation -> CachedTrack (PCM / waveform / tags)
                 |
        PlayerLibrary + PlaybackQueue
                 |
          QueuePlaybackSession
                 |
            QueueSource
                 v
        MasterEqualizerSource
                 |
          MixedPlayback -> PcmMixer -> MasterSpectrum
                 |
            IAudioOutput
          /             \
 Android AudioTrack   Windows WaveOutEvent

MainPage -> PlayerView / DeckView / TrackQueueView
         -> PlatformSystemPlaybackIntegration
```

| Слой | Ответственность | Точки входа |
|---|---|---|
| Базовая DSP-логика | Стереосведение, PCM-контракт, частотный анализ | [`IPcmSource`](src/Laveyana.Audio/IPcmSource.cs), [`PcmMixer`](src/Laveyana.Audio/PcmMixer.cs), [`MasterSpectrum`](src/Laveyana.Audio/MasterSpectrum.cs) |
| Подготовка и воспроизведение | Декодирование, кэш, деки, эквалайзер, интерфейс аудиовывода | [`TrackPreparation`](src/Laveyana.Audio.Playback/Tracks/TrackPreparation.cs), [`QueuePlaybackSession`](src/Laveyana.Audio.Playback/Session/QueuePlaybackSession.cs), [`IAudioOutput`](src/Laveyana.Audio.Playback/Session/IAudioOutput.cs) |
| Приложение | Медиатека, порядок очереди, команды, жизненный цикл платформы | [`MainPage`](samples/Laveyana.Audio.Maui/MainPage.cs), [`PlayerLibrary`](samples/Laveyana.Audio.Maui/Tracks/PlayerLibrary.cs), [`PlaybackQueue`](samples/Laveyana.Audio.Maui/Tracks/PlaybackQueue.cs) |
| Представления | MAUI-компоненты на C# и обработка жестов | [`PlayerView`](src/Laveyana.Audio.Maui.Controls/Views/PlayerView.cs), [`DeckView`](src/Laveyana.Audio.Maui.Controls/Views/DeckView.cs), [`TrackQueueView`](src/Laveyana.Audio.Maui.Controls/Views/TrackQueueView.cs) |

<details>
<summary>Подробности аудиоядра и очереди</summary>

[`QueueSource`](src/Laveyana.Audio.Playback/Session/QueueSource.cs) владеет PCM-ридерами текущего и подготовленного следующего треков, управляет перемоткой и скоростью, после перехода переносит входящую деку в текущую. Ручной переход запрашивается до следующего аудиоблока. [`MasterEqualizerSource`](src/Laveyana.Audio.Playback/Session/MasterEqualizerSource.cs) фильтрует уже **сведённый** сигнал с помощью десяти полос пиковых фильтров и каскада усиления; при изменении настроек плавно переключаются состояния фильтров. [`MixedPlayback`](src/Laveyana.Audio.Playback/Session/MixedPlayback.cs) формирует блоки и публикует результаты FFT с учётом позиции фактического аудиовывода, когда платформа её предоставляет.

[`PlayerLibrary`](samples/Laveyana.Audio.Maui/Tracks/PlayerLibrary.cs) ограничивает количество параллельных декодирований, объединяет одинаковые запросы на подготовку и вытесняет старые PCM-файлы при превышении бюджета 256 МиБ, сохраняя данные занятых дек. Приложение последовательно подготавливает добавленные записи в фоне; выбранный пользователем трек может использовать ещё один слот декодирования. Упорядоченная [`PlaybackQueue`](samples/Laveyana.Audio.Maui/Tracks/PlaybackQueue.cs) не переставляет записи при выборе другого трека: текущая позиция определяет границу прослушанной истории.

</details>

## Навигация по репозиторию

```text
src/Laveyana.Audio/                 PCM mixing and FFT
src/Laveyana.Audio.Playback/        MP3 preparation, cache, queue mixer, EQ
src/Laveyana.Audio.Maui.Controls/   C# player UI and platform gestures
samples/Laveyana.Audio.Maui/       MAUI host, media library, settings, output adapters
tests/Laveyana.Audio.Playback.Checks/  Executable regression checks
```

[`MainPage`](samples/Laveyana.Audio.Maui/MainPage.cs) владеет состоянием приложения и подписками UI. Отдельные сценарии расположены в `MainPage.Import.cs`, `MainPage.Queue.cs`, `MainPage.Analysis.cs`, `MainPage.Transport.cs`, `MainPage.Rendering.cs`, `MainPage.SystemPlayback.cs`, `MainPage.Dialogs.cs` и `MainPage.State.cs`. Реализация карточки обложки очереди находится в [`TrackQueueView.TrackCard.cs`](src/Laveyana.Audio.Maui.Controls/Views/TrackQueueView.TrackCard.cs).

## Основные потоки выполнения

```text
AddFilesAsync -> PlayerLibrary.Add -> LoadFirstAsync
              -> TrackPreparation.ImportAsync -> CachedTrack
              -> EnqueueBackgroundAnalysis -> AnalyzeQueueSequentiallyAsync

ChooseAsync -> TrySetSelection -> ArmAsync -> StartMixAsync
            -> QueuePlaybackSession.RequestMix -> QueueSource

QueuePlaybackSession.RunAsync -> MixedPlayback.Render
            -> QueueSource -> MasterEqualizerSource -> PcmMixer
            -> IAudioOutput -> native device
```

## Публичный API

Библиотеки DSP и воспроизведения **не зависят от MAUI**. Внешний хост передаёт реализацию [`IAudioOutput`](src/Laveyana.Audio.Playback/Session/IAudioOutput.cs); Android- и Windows-реализации в приложении имеют внутреннюю область видимости.

```csharp
using Laveyana.Audio.Playback;

static async Task PlayFileAsync(
    string mp3Path, string cacheDirectory, IAudioOutput output,
    CancellationToken token)
{
    var preparation = new TrackPreparation(cacheDirectory);
    var track = await preparation.ImportAsync(TrackFile.FromPath(mp3Path), token);
    try
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(track);
        session.SetEqualizer(EqualizerSettings.Flat.WithEnabled(true).WithGain(0, 3));
        await session.RunAsync(output, token);
    }
    finally
    {
        track.Delete();
    }
}
```

[`PcmMixer`](src/Laveyana.Audio/PcmMixer.cs) может также сводить независимые входы `IPcmSource` с одинаковой частотой дискретизации, без очереди. Частоты десяти полос опубликованы в [`EqualizerSettings.Frequencies`](src/Laveyana.Audio.Playback/Session/EqualizerSettings.cs).

<details>
<summary>Контракт интеграции аудиовывода</summary>

`IAudioOutput.PlayAsync(IAudioRenderSource, CancellationToken)` многократно запрашивает чередующиеся стереосэмплы через `IAudioRenderSource.Render`. Нельзя вызывать `Render` одновременно из нескольких потоков; при наличии счётчика воспроизведённых кадров следует передавать их через `ReportOutputPosition`. Текущий формат определён в [`PlaybackFormat`](src/Laveyana.Audio.Playback/Session/PlaybackFormat.cs): 48 000 Гц, два канала, PCM16 на выходе, блоки по 1 024 кадра. `CachedTrack` владеет декодированным PCM-файлом; удалять его можно только после завершения чтения всеми воспроизводящими сессиями.

Для однократного воспроизведения двух независимо управляемых дек [`PlaybackSession`](src/Laveyana.Audio.Playback/Session/PlaybackSession.cs) принимает аудиоадаптер, `DeckVolumes`, подготовленные треки и начальные позиции. Для воспроизведения очереди `QueuePlaybackSession` сохраняет позиции дек между паузой и продолжением и предоставляет `SetNext`, `RequestMix`, `Seek`, `TrySetSpeed` и `RequestPause`.

</details>

## Жизненный цикл и потоки

- `MainPage` владеет `PlayerLibrary`, `PlaybackQueue`, `QueuePlaybackSession` и мостом системного плеера; `CloseAsync` отменяет и дожидается завершения воспроизведения и анализа перед удалением PCM-кэша.
- Импорт и расчёт волн выполняются асинхронно с ограниченным числом декодеров. Нативный аудиовыход получает PCM в собственном потоке; C# MAUI-представления и состояние очереди обновляются через UI-диспетчер.
- У микшера один поток вызова `Render`. `QueueSource` синхронизирует изменения текущей и следующей дек; последний снимок для UI читается отдельно. Соответствие декодированных треков их файлам хранится в потокобезопасном словаре.
- Пауза и сворачивание различаются: сворачивание не отменяет работу микшера. Android использует фоновый медиасервис, Windows публикует System Media Transport Controls, iOS использует AVAudioSession и Now Playing.

<details>
<summary>Производительность и владение ресурсами</summary>

Бюджет кэша относится к декодированному PCM, а не к исходным MP3. Это ограничение вытеснения, а не гарантия постоянной готовности всех треков очереди. Ранее вытесненный трек может потребовать повторного декодирования. Строки очереди виртуализированы; полноразмерные обложки ID3 уменьшаются до миниатюр. Геометрия волны кэшируется отдельно от указателя; анализатор спектра отключается при скрытии мастер-спектра. Визуальные оптимизации не должны менять порядок или временные характеристики аудиоблоков.

Платформенные адаптеры: [Android `PlatformAudioOutput`](samples/Laveyana.Audio.Maui/Platforms/Android/PlatformAudioOutput.cs) и [Windows `PlatformAudioOutput`](samples/Laveyana.Audio.Maui/Platforms/Windows/PlatformAudioOutput.cs). [`AndroidMediaPlaybackService`](samples/Laveyana.Audio.Maui/Platforms/Android/AndroidMediaPlaybackService.cs) предоставляет системные кнопки Android, а [`WindowsMediaIdentity`](samples/Laveyana.Audio.Maui/Platforms/Windows/WindowsMediaIdentity.cs) регистрирует идентификатор приложения Windows без MSIX.

</details>

## Проверки

[`Laveyana.Audio.Playback.Checks`](tests/Laveyana.Audio.Playback.Checks/Program.cs) — исполняемый консольный набор регрессионных проверок, **не** проект xUnit/NUnit:

```bash
dotnet run --project tests/Laveyana.Audio.Playback.Checks -c Release
```

Проверяются сведения о MP3-кадрах, захват спектра, плавные переходы транспорта, перемотка/скорость, переходы и история очереди, повтор трека, мастер-эквалайзер и очистка после ошибки аудиовывода. Детерминированные проверки аудиоинвариантов следует добавлять сюда; нативные жесты MAUI, фоновое воспроизведение и системные медиасессии проверяются на соответствующих устройствах. [`build.yml`](.github/workflows/build.yml) запускается вручную. [Проверка iOS](.github/workflows/ios.yml) собирает приложение и проверяет запуск на симуляторе iPhone. Для ручной проверки iOS импортируй два MP3 из «Файлов», проверь сведение, эквалайзер и перемотку, затем заблокируй экран и проверь кнопки Now Playing.

## Ограничения

- Импорт поддерживает локальные **MP3**, а не потоковые сервисы или произвольные кодеки. Декодирование принимает моно/стерео MP3 с исходной частотой до 48 кГц.
- Изменение скорости деки выполняется ресэмплингом и **меняет высоту звука**; сохранение тона при изменении темпа и автоматическое совмещение битов здесь не реализованы.
- Микшер содержит две деки воспроизведения и один итоговый стереовыход. Перечисление или синхронизация нескольких физических аудиоустройств не реализованы.
- Рекомендации пока представлены заглушкой в UI.
- В Windows буферизация WaveOut может добавлять задержку; системные кнопки и фоновое воспроизведение зависят от ОС и устройства.

## Точки расширения

| Задача | С чего начать |
|---|---|
| Добавить декодер или новый источник данных | [`IPcmSource`](src/Laveyana.Audio/IPcmSource.cs), [`TrackPreparation`](src/Laveyana.Audio.Playback/Tracks/TrackPreparation.cs) |
| Изменить сведение, паузу или скорость дек | [`QueueSource`](src/Laveyana.Audio.Playback/Session/QueueSource.cs), [`TransportFadeSource`](src/Laveyana.Audio.Playback/Session/TransportFadeSource.cs) |
| Изменить эквалайзер или спектральный анализ | [`MasterEqualizerSource`](src/Laveyana.Audio.Playback/Session/MasterEqualizerSource.cs), [`MasterSpectrum`](src/Laveyana.Audio/MasterSpectrum.cs) |
| Изменить очередь, историю или фоновую подготовку | [`MainPage.Queue.cs`](samples/Laveyana.Audio.Maui/MainPage.Queue.cs), [`MainPage.Analysis.cs`](samples/Laveyana.Audio.Maui/MainPage.Analysis.cs), [`PlaybackQueue`](samples/Laveyana.Audio.Maui/Tracks/PlaybackQueue.cs) |
| Изменить жесты, волны или компоновку плеера | [`DeckView`](src/Laveyana.Audio.Maui.Controls/Views/DeckView.cs), [`TrackQueueView`](src/Laveyana.Audio.Maui.Controls/Views/TrackQueueView.cs), [`WaveformDrawable`](src/Laveyana.Audio.Maui.Controls/Views/WaveformDrawable.cs) |
| Добавить нативный выход или системную медиасессию | [`IAudioOutput`](src/Laveyana.Audio.Playback/Session/IAudioOutput.cs), [`SystemPlaybackIntegration`](samples/Laveyana.Audio.Maui/SystemPlaybackIntegration.cs) |

## Зависимости и лицензия

LavPulse распространяется под лицензией [MIT](LICENSE). Основные внешние зависимости: [NLayer 3.0.1](https://www.nuget.org/packages/NLayer/3.0.1) (декодирование MP3, MIT), [NAudio.WinMM 2.2.1](https://www.nuget.org/packages/NAudio.WinMM/2.2.1) (Windows WaveOut, MIT) и [Microsoft.Maui.Controls](https://github.com/dotnet/maui) (кроссплатформенный UI, MIT). Встроенный шрифт Inter распространяется по SIL Open Font License; текст лицензии включён в [`Inter-OFL.txt`](samples/Laveyana.Audio.Maui/Resources/Fonts/Inter-OFL.txt). При распространении зависимостей или шрифтов необходимо сохранять соответствующие уведомления об авторских правах и лицензиях.
