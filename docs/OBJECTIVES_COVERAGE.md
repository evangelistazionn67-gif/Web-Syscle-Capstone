# How Web-Syscle meets the paper's objectives (v8)

| Objective | Where in the system |
|---|---|
| 1. Automated inventory tracking | Admin > Inventory (add/edit/delete, dropdown type/size/color, no duplicates), Stock movement log, automatic stock deduction/return on orders, low-stock alerts on the Dashboard |
| 2. Daily, weekly, monthly (and yearly) sales reports | Admin > Reports: Daily / Weekly / Monthly / Yearly, comparison with the previous period, per-item, CSV and print |
| 2. Identify fast and slow-moving products | Admin > Product insights: Fast-moving / Slow-moving table (last 14-90 days) |
| 2. Forecasts | Admin > Product insights: 7-day / 30-day sales forecast chart + restock suggestions |
| 3. Bundle promotions | Admin > Bundles (manual, BOGO, auto-bundles of slow items) |
| 3. Discount promotions | Admin > Promo codes (percent off, start/end date, usage limit) |
| 3. Pre-built bike packages | Admin > Bundles > Type "Complete bike package" |
| 3. Featured product promotions | "Feature" star on Inventory and on Product insights; shown in the Featured row on the landing page and shop homepage |
| 3. Seasonal promotions | Season tag + start/end dates on promo codes and bundles; seasonal codes appear as a banner for customers |

Notes for the paper
- Forecast = weekday averages of the last 8 weeks, adjusted by the recent 4-week trend. It is a simple estimate, not machine learning.
- Fast = top quarter of sellers in the period. Slow = in stock and sold 1 or fewer in the period.
- Only promo codes with a season tag are shown publicly; codes without one stay private (for frequent buyers).
- The database updates itself on start (new columns: PromoCodes StartsAt/EndsAt/MaxUses/Season, Bundles StartsAt/EndsAt/Season/Kind, Products IsFeatured).
- C# is not compiled or tested here. Build once and fix anything the compiler reports.
