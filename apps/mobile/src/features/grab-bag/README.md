# Grab bag in the fan app

`/grab-bag` (`GrabBagScreen.tsx`): your five favourite songs in numbered slots (`GET /me/grab-bag`). Search Spotify
for a song (`GET /tracks/search`) and Add puts it in the first empty slot (`PUT /me/grab-bag/{position}`); Remove
empties a slot. Each song opens in Spotify. Adding a song by an artist who isn't on Fandom Score yet adds that
artist (the screen says so, and marks the artist "getting ready" until their albums are loaded).

Followers see the songs on their feed with your name (decided 2026-10-20; can't be turned off). Reached from the
profile ("Your grab bag").

Tests: `GrabBagScreen.test.tsx`.
