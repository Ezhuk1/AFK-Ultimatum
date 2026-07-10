# AutoChooser

Это плагин для [ExileApi](https://github.com/exApiTools/ExileApi-Compiled) (PoE HUD).

Плагин следит за появлением определённого игрового меню. Когда меню открыто,
он выбирает один из вариантов согласно заданному приоритету и нажимает кнопку старта.

Заточен под механику **Ultimatum**: при входе в испытание появляется панель с
**тремя карточками опций в ряд** и одной кнопкой подтверждения («Begin» / «Start»).
Плагин выбирает одну карточку по приоритету и нажимает кнопку старта.

> Раскладка Ultimatum (подтверждено сторонним проектом
> [curtis91791/POE_Ultimatum](https://github.com/curtis91791/POE_Ultimatum)):
> 3 опции располагаются горизонтально, ниже — одна кнопка принятия.
> Примеры реальных имён модификаторов-опций: `Reduced Recovery`,
> `Shattered Shield`, `Restless Ground` (зависит от патча/языка клиента).

## Установка

- Скопируйте скомпилированный `AutoChooser.dll` в папку
  `PoeHelper\Plugins\Compiled\AutoChooser`.
- Включите плагин в списке плагинов ExileApi.

## Настройка (меню внутри игры)

В окне настроек плагина ExileApi доступно:

| Параметр | Назначение |
|----------|------------|
| **Enable** (чекпоинт) | Главный вкл/выкл плагина. |
| **Menu title / identifier text** | Текст, по которому определяется нужное меню. Если пусто — срабатывает по кнопке старта + наличию любой опции. |
| **Start button text** | Текст кнопки запуска (по умолчанию `Begin`). |
| **Avoid threshold** | Приоритет `>=` этого значения означает «никогда не брать» (по умолчанию `40`). |
| **Force pick when all avoided** | Если все 3 видимые опции «запрещены», всё равно взять лучшую (чтобы не застрять). |
| **Delay before clicking option (ms)** | Пауза после открытия меню перед выбором. |
| **Delay between option and start (ms)** | Пауза между выбором и нажатием старта. |
| **Retry start press interval (ms)** | Интервал повтора нажатия старта, пока меню открыто. |
| **Debug logging** | Лог координат кликов. |
| **Dump texts** | Вывод всех текстов меню при открытии (узнать точные строки). |

Ниже в том же окне — список **всех модификаторов Ultimatum**, у каждого
ползунок **Priority (1–100)**:

- **1** — самый высокий приоритет, берём всегда.
- Чем больше число — тем менее желательна опция.
- **≥ Avoid threshold (40)** — никогда не берём.

## Пример (Ultimatum)

В Ultimatum горизонтально **3 карточки**. Плагин из присутствующих трёх
выбирает ту, у которой **наименьший** приоритет (выше в списке), если он
`< 40`. То есть:

- Поставь `Shattered Shield`, `Reduced Recovery`, `Stormcaller Runes` и т.п.
  на значение **40 и выше** → они никогда не выберутся.
- Поставь желанные (`Restless Ground`, `Quicksand`, `Ruin` …) на **1–10**.
- Остальные оставь на дефолтном **20** — возьмутся, если нет ничего лучше.

Результат: из трёх карточек запрещённые (`>=40`) отбрасываются; среди
оставшихся берётся та, у которой приоритет меньше (например, `Restless Ground`
с приоритетом 5).

### Точные названия карточек Ultimatum (poedb.tw)

Это текст на карточке (без суффикса тира «II/III/IV» — плагин матчит по
подстроке). Для русского клиента — аналоги со `poedb.tw/ru/Ultimatum`.

| Категория | Названия (англ.) |
|-----------|------------------|
| Окружение (DoT/ловушки) | `Choking Miasma`, `Stormcaller Runes`, `Raging Dead`, `Blistering Cold`, `Restless Ground`, `Stalking Ruin`, `Razor Dance`, `Quicksand`, `Blood Altar` |
| Тотемы | `Totem of Costly Might`, `Totem of Costly Potency` |
| Босс/арена | `The Trialmaster`, `Limited Arena` |
| Руин | `Ruin` |
| Дебаффы игрока | `Reduced Recovery`, `Lessened Reach`, `Buffs Expire Faster`, `Less Cooldown Recovery`, `Escalating Damage Taken`, `Escalating Monster Speed`, `Profane Monsters`, `Unlucky Criticals`, `Hindering Flasks`, `Drought`, `Ailment and Curse Reflection`, `Lightning Damage from Mana Costs`, `Random Projectiles`, `Treacherous Auras`, `Occasional Impotence`, `Siphoned Charges`, `Impurity`, `Waning Spirit` |
| Усиление монстров | `Shattered Shield`, `Unstoppable Monsters`, `Lethal Rare Monsters`, `Shielding Monsters`, `Precise Monsters`, `Overwhelming Monsters`, `Deadly Monsters`, `Prismatic Monsters`, `Resistant Monsters`, `Dexterous Monsters`, `Siphoning Monsters`, `Putrid Monsters`, `Impenetrable Monsters` |

Все эти названия уже заранее добавлены в список настроек со значением
приоритета **20** — нужно только подкрутить ползунки под себя.

## Как узнать точные тексты (важно)

Точные строки зависят от языка клиента и патча. Чтобы их узнать:

1. Включи **Dump texts**.
2. Открой меню Ultimatum в игре (не выбирая ничего).
3. В логах ExileApi (`DebugWindow` / консоль) появятся строки
   `AutoChooser [text]: '...'`.
4. Скопируй реальные заголовки карточек и текст кнопки, сопоставь со
   списком и выстави нужные приоритеты.

> Совет: текст ищется по **подстроке** без учёта регистра, поэтому базового
> имени (например, `Raging Dead`) достаточно — оно совпадёт и с `Raging Dead IV`.

## Примечания

- Поиск элементов выполняется по подстроке (без учёта регистра).
- Если точное имя меню неизвестно, оставьте **Menu title** пустым.
- Для Ultimatum обычно достаточно выставить приоритеты опций и
  **Start button text**; **Menu title** можно оставить пустым.
