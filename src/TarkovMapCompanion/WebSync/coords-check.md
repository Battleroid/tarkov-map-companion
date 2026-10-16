<!--
  Deviation from docs/websync-implementation-spec.md section 7.3, which literally asks for
  WebSync/Assets/coords-check.md: this file deliberately lives in WebSync/ (not Assets/) so that
  the csproj's <EmbeddedResource Include="WebSync\Assets\**" /> glob does not embed a docs file
  into the shipped assembly. The client lane owns WebSync/Assets/.
-->

# WebSync client coordinate check (game -> base)

The browser client ports `MapProjection.ToBase` to JavaScript as `gameToBase`. There is no way to
run JS from the C# test suite, so these are worked values, computed by calling the real
`MapProjection.ToBase` (via `GameMap.Projection`) for a real map, for a human to reproduce in the
browser console.

## Map and affine

Map: `factory` (normalized name `factory`, display name Factory). Its `coordinateRotation` is `90`,
so it exercises the rotate-then-scale path with a genuinely off-axis rotation rather than the
common 180-degree one.

Coefficients, straight from `map.projection` in `GET /api/state`:

| field | value |
|---|---|
| cos | 6.123233995736766e-17 |
| sin | 1 |
| a | 1.629 |
| b | 119.9 |
| c | 1.629 |
| d | 139.3 |
| coordinateRotationDegrees | 90 |

## The JS function (from spec section 5.2)

```js
function gameToBase(x, z, p) {
  const rx = x * p.cos - z * p.sin;
  const ry = x * p.sin + z * p.cos;
  return { x: p.a * rx + p.b, y: -p.c * ry + p.d };
}
```

## Worked cases

| # | gameX | gameZ | expected baseX | expected baseY | console check |
|---|---|---|---|---|---|
| 1 | 0 | 0 | 119.900000 | 139.300000 | `gameToBase(0, 0, p)` |
| 2 | 100 | 0 | 119.900000 | -23.600000 | `gameToBase(100, 0, p)` |
| 3 | 0 | 100 | -43.000000 | 139.300000 | `gameToBase(0, 100, p)` |

To get `p` in the console:

```js
const state = await (await fetch('/api/state?key=' + sessionStorage.getItem('key'))).json();
const p = state.map.projection;
gameToBase(100, 0, p);   // -> { x: 119.9, y: -23.6 }
```

These expected values come from the real `MapProjection.ToBase(double, double)`. For case 2 the
machine prints `119.90000000000002` and `-23.599999999999994`, and for case 3 `-43`; the table is
rounded for legibility. Compare with a small tolerance (about `1e-6`), not exact equality: the
browser reproduces the same formula but not necessarily the same floating-point rounding.

Note: `cos` is `6.12e-17`, i.e. zero to within double precision. A JS port that treats it as exactly
`0` matches; one that substitutes a non-zero constant does not.
