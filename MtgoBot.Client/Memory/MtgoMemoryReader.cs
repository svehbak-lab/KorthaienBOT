// ── PATCH: FilterUserOffersAsync — canBuy fix ──────────────────────
//
// BUG (original):
//   int canBuy = Math.Max(0, maxStock - currentStock + redeemReserved - currentStock);
//   → currentStock subtracted TWICE, always underestimates capacity.
//
// FIX:
//   int canBuy = Math.Max(0, maxStock - currentStock);
//   redeemReserved units are already accounted for in maxStock via DB config.
//   If you want the bot to BUY extras specifically to cover redeem-reserved slots,
//   the correct formula is:
//   int canBuy = Math.Max(0, (maxStock + redeemReserved) - currentStock);
//
// Replace the relevant lines in TradeEngine.cs:

// ── BEFORE (in FilterUserOffersAsync) ──────────────────────────────
//
//   int canBuy = Math.Max(0, maxStock - currentStock + redeemReserved - currentStock);
//
// ── AFTER ──────────────────────────────────────────────────────────
//
//   int canBuy = Math.Max(0, (maxStock + redeemReserved) - currentStock);
//
// This means:
//   - maxStock   = how many to hold for normal trading
//   - redeemReserved = extra units needed for set redemption
//   - currentStock = what the bot already has
//   - canBuy = how many more units are needed total
