# Tonal analysis and sample-rate conversion

The chroma branch reduces 48 kHz stereo PCM to 12 kHz before its 8192-point FFT. Energy near 12 kHz can fold into the 55–2200 Hz chroma band if the low-pass filter is weak. A generated progression near 12 kHz produced a C major decision with about 0.92 confidence in the previous implementation.

Local-key analysis version 2 uses a fourth-order Butterworth low-pass at 3 kHz and checks that chroma-band power is within 40 dB of broadband input power. The gate is relative to the input, so lowering the volume of a band-limited progression does not trigger it. A frequency-domain power correction retains the previous chroma weighting inside the passband. The filter state persists across PCM blocks. Stereo power and analysis window positions retain their existing interfaces. The current cache can still read results from version 1. Rescanning computes the revised result.

Regression checks verify the alias-only progression, a quiet C major progression, the previous cache version and the existing anti-phase stereo cases. This covers the decimation fault. It does not measure key accuracy on music with a known reference.

## Тональность и понижение частоты дискретизации

Перед FFT на 8192 точки тональная ветка переводит стереосигнал 48 кГц в 12 кГц. При слабой фильтрации энергия около 12 кГц отражается в диапазон хромы от 55 до 2200 Гц. Синтетическая последовательность с частотами около 12 кГц давала оценку C major с уверенностью около 0,92 в предыдущей реализации.

Версия 2 использует ФНЧ Баттерворта четвёртого порядка с частотой 3 кГц. Мощность в полосе хромы должна отставать от широкой полосы входа максимум на 40 дБ. Порог относительный и пропускает тихую последовательность с энергией внутри полосы. Поправка мощности в спектре сохраняет прежние веса хромы в рабочей полосе. Состояния фильтра сохраняются между блоками PCM. Старые записи кэша версии 1 читаются. Повторное сканирование рассчитывает новую оценку.

Регрессионные проверки охватывают свёрнутый спектр, тихую аккордовую последовательность, предыдущую версию кэша и противофазное стерео. Для измерения точности тональности на музыке нужен корпус с известной тональностью.
