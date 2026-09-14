# Design system

Cadence uses the palette and type system from [fan-zhu.com](https://fan-zhu.com), ported from CSS
custom properties to WPF resource dictionaries. This note records what was carried across, what had
to change for a desktop app, and the rules to keep if you touch the UI.

## Where things live

| File | Holds |
|---|---|
| `Theme/Tokens.xaml` | Type stacks, size scale, tracking, geometry, motion. Theme-independent. |
| `Theme/Dark.xaml` | Dark palette. The default. |
| `Theme/Light.xaml` | Light palette. |
| `Theme/Controls.xaml` | Text and button styles built on the tokens. |
| `Theme/FormControls.xaml` | Retemplated stock controls: combo, check, tabs, scrollbar, grid. |
| `Theme/Tracking.cs` | Letter-spacing, which WPF does not have. |
| `Theme/ThemeManager.cs` | Swaps the palette dictionary and re-renders tray icons. |

`ThemeManager` merges exactly one of Dark/Light at index 0, so every brush is a `DynamicResource`
lookup and a theme switch needs no restart.

## The palette

Values are copied verbatim from the source, including the contrast ratios in the comments. Every
ink and accent pair clears WCAG AA against its own surface; if you change one, re-check it rather
than eyeballing it.

The part worth understanding is the **two-tone accent**. There is one hue in two strengths:

- `AccentBrush` — the dominant, current term. The forecast owns this: prediction bands, the hatch
  drawn beyond the fill, the one action that fixes an error.
- `Accent2Brush` — the receded term. Structural labels and eyebrows.

The roles **invert between themes**: in dark, the dominant tone is the *lighter* blue; in light it
is the *darker* one. Getting that backwards makes labels shout over the forecast, which inverts the
whole information hierarchy.

`BullBrush` and `BearBrush` are the semantic pair. Bear is overrun and terminal errors. Bull is
currently unused in the UI and is there for headroom states.

## Type

Three stacks, written as full fallback chains exactly as the source declares them:

```
Display   Libre Franklin, Franklin Gothic Medium, Segoe UI Variable Display, Segoe UI
Body      Inter, Segoe UI Variable Text, Segoe UI
Mono      IBM Plex Mono, Cascadia Mono, Consolas
```

None of the three preferred faces ship with Windows. Franklin Gothic Medium and Cascadia Mono do,
and they are the source's own next choices, so the app looks right out of the box and upgrades
silently if anyone installs Inter or IBM Plex Mono.

**The rule that matters:** structure is spoken in monospace, uppercase and tracked; content is
spoken in the body face. If a label *names* a thing rather than *being* the thing, it is mono —
`RESETS IN 3 HOURS`, `TODAY`, `CLAUDE · MAX 5X`. The forecast sentence underneath a bar is content,
so it is body text.

Every number is tabular and right-aligned. A value moving from 9% to 17% must not shift its column.

## Letter-spacing

WPF has no `letter-spacing`. `Typography` exposes OpenType features, not tracking, and `TextBlock`
gives no hook into glyph advances short of `Glyphs` or a custom `TextFormatter`.

`Theme/Tracking.cs` rebuilds the text as alternating character and spacer runs, each spacer a space
rendered at a size that yields the wanted advance. Inserting space *characters* does not work here:
the labels are monospaced, so every space is a full cell wide.

**Bind `Tracking.Text`, never `Text`, on a tracked label.** Writing to `Inlines` assigns `Text` as a
local value, which clears any binding on it — the label renders once and never updates again. That
bug reads as stale data rather than as a broken binding, so it is easy to miss and hard to find.

## Geometry

4px radius, not 8. The source system is deliberately square-ish, which reads as instrument rather
than as consumer app. Bars are 2px, controls 3px, surfaces 4px.

Grouping is done with **hairlines**, not boxes. Provider cards are separated by a 1px rule and
marked with a 2px left rule in the provider's own colour — the only place a provider brand appears
at any size, so three brand hues never fight the navy.

## If you add a surface

- Paint an explicit background from a token. WPF gives a transparent window the host's colour.
- Use `DynamicResource` for every brush, or your surface will not follow a theme change.
- Retemplate any stock control you introduce. The Aero-era defaults are light chrome and read as
  broken on a dark ground; `FormControls.xaml` is where a new one belongs.
- Check both themes before calling it done. `--screenshot-window settings <path>` and
  `--screenshot`/`--screenshot-hud` render a surface to PNG without needing to catch it on screen.
