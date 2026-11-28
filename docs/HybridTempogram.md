# Diagnostic hybrid tempogram

Trace schema 18 adds beat and state CSVs for the hybrid tempogram. The path receives five onset bands at 100 Hz. It combines autocorrelation at one, two and three periods with the magnitude of the Fourier tempogram. For each two-second frame it averages the results of eight- and sixteen-second Hann windows. The previous audit baseline uses the same sixteen-second autocorrelation as before. Both paths integrate their tempo and use the same phase decoder.

The Fourier power is taken from the forward FFT used for autocorrelation. Only the shorter window introduces another FFT pair per band. It shares the same bounded scratch buffers. The diagnostic decoder already runs only on explicit report analysis. The new path is exported for comparison and does not enter the trained ranker, waveform cache, or DJ playback.

On an independent subset of 24 GuitarSet microphone recordings, the frozen Python hybrid reached mean F1 of 0.593 at 70 ms, compared with 0.550 for ACF alone and 0.333 for the C# confirmed BeatGrid. These figures do not establish the accuracy of this C# port. They motivate testing the audio-clock implementation using new reports and annotated music. GuitarSet v1.1.0 is by Xi et al. and distributed under CC BY 4.0.

## Гибридная темпограмма в отчётах

В схеме трассировки 18 добавлены CSV состояний и долей гибридной темпограммы. Из пяти полос атак на частоте 100 Гц вычисляются автокорреляция на одном, двух и трёх периодах и модуль темпограммы Фурье. Путь использует среднее оценки в окнах 8 и 16 секунд и обновляет темп раз в 2 секунды. Базовый экспериментальный путь сохраняет прежнюю 16-секундную автокорреляцию. Два пути используют один алгоритм интегрирования темпа и оценки фазы.

Мощность частот берётся из прямого FFT, который уже нужен автокорреляции. Дополнительную пару преобразований требует только короткое окно в каждой полосе. Оба окна используют одни буферы. Путь доступен в диагностическом экспорте для сравнения. Публичная сетка, кэш и выбор автомикса используют прежние алгоритмы.

На 24 независимых записях GuitarSet средний F1 при 70 мс у Python-гибрида составил 0,593, у чистой автокорреляции 0,550, у подтверждённого C# BeatGrid 0,333. Эти числа относятся к указанным моделям. Для C#-реализации нужны сравнение на том же потоке признаков и новые тестовые записи.

[GuitarSet v1.1.0, Xi et al., CC BY 4.0](https://doi.org/10.5281/zenodo.3371780)
