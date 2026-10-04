IDEAL MARKET PRICE - Supermarket Together / BepInEx 5

WHAT IT DOES
------------
Sets every available product's selling price to:

    purchase market price * 2

That is a 100% markup: if the market/wholesale price is 5.00, the shop sells for 10.00 and the gross profit is 5.00 per unit.

CONTROLS
--------
F5 = manually update prices (host/server only).

The mod also updates after GameData.RpcStartDay on the host.

SAFETY
------
- Null checks for ProductListing and price data.
- Per-product try/catch so one bad product does not stop the rest.
- Global try/catch around the update.
- The RpcStartDay postfix catches its own exceptions and does not let them escape into the day transition.
- Price changes run in a Unity coroutine after a short delay instead of blocking the main thread.

BUILD
-----
Open a Developer Command Prompt in this folder and run:

    dotnet build -p:GameDir="C:\Path\To\Supermarket Together"

The resulting DLL is automatically copied to:

    BepInEx\plugins\IdealMarketPrice.dll

IMPORTANT
---------
The project must use the same game's Assembly-CSharp.dll as the installed game.
The included source was written against the uploaded Assembly-CSharp.dll structure.
