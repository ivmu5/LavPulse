# Phase agreement in diagnostic reports

Version 2 estimates the relative phase in the part of an eight-second block where each grid has regular inter-beat intervals. A point is evaluated using its fractional position within the other grid's surrounding interval. Intervals outside 0.67–1.5 times the median are excluded. Each direction contributes its mean circular vector, and the two vectors have equal weight. Reversing the comparison reverses the phase offset. An exactly half-period offset has equivalent signed representations.

The status `insufficient_overlap` means fewer than three phase observations in either direction. The strict 70 ms match count remains useful even if the phase estimate is unavailable. In version 1, separated grids could report concentration 1 and offset 0 because multiple beats were compared to the same endpoint and all gaps were integer multiples of a period. The CSV columns retain their existing format. The diagnostic metadata records the comparison version.

## Согласие фаз в диагностических отчётах

Версия 2 сравнивает сетки в общей части восьмисекундного блока. Положение доли определяется как дробная координата в охватывающем интервале второй сетки. Интервалы короче 0,67 или длиннее 1,5 медианного периода исключаются. Средние круговые векторы двух направлений имеют одинаковый вес. Если поменять сетки местами, знак смещения меняется. Для сдвига ровно на полпериода оба знака эквивалентны.

Статус `insufficient_overlap` означает меньше трёх наблюдений фазы хотя бы в одном направлении. Счётчик строгих совпадений с допуском 70 мс вычисляется отдельно. В версии 1 разнесённые сетки могли получить концентрацию 1 и сдвиг 0, если несколько долей сопоставлялись с одной границей через целое число периодов. Версия сравнения указана в метаданных. Столбцы CSV сохраняют прежний формат.
