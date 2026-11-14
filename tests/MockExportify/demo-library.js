// The made-up library fake-spotify.js serves with ?demo, for screenshots. The object below is plain JSON, so
// tests/Update-ReadmeScreenshots.ps1 can read it too. Track i of a playlist is title (seed + 5i) and artist
// (seed + 7i), where seed is the playlist ID hashed as fake-spotify.js's demoSeed does.
window.demoLibrary = {
    "artists": [
        ["Halcyon Bay", "Northern Lights", "2021-03-12"],
        ["Nova Lindqvist", "Paper Moons", "2019-09-27"],
        ["The Copper Kites", "Long Way Home", "2018-05-04"],
        ["Marisol Quinn", "Blue Hour", "2022-01-21"],
        ["Static Orchard", "Small Hours", "2017-10-13"],
        ["Wren & Ashby", "Field Notes", "2020-06-19"],
        ["Tall Pines", "Wide Open", "2016-08-26"],
        ["Ivy Calloway", "Glasshouse", "2023-04-07"],
        ["Dusty Atlas", "Mile Markers", "2015-07-10"],
        ["Golden Hour Club", "Late Bloom", "2021-11-05"],
        ["Juno Marlowe", "Satellite Hearts", "2019-02-15"],
        ["The Lantern Room", "After the Rain", "2018-12-07"]
    ],
    "titles": [
        "Open Road", "Neon Skyline", "Coastline", "Slow Burn", "Highway Hum", "Weekend Sun", "Paper Boats", "Silver Linings",
        "Back Roads", "City Lights Fade", "Wildflower", "Window Down", "Northbound", "Sunday Morning Light", "Postcards",
        "Echoes in the Valley", "Lighthouse", "Gravel & Gold", "Late Night Diner", "Summer Static", "Firefly", "Driftwood",
        "Miles Away", "Starlight Motel", "Homeward", "Long Way Round", "Parachute", "Polaroid", "Evergreen", "Night Swim",
        "Tidal", "Kaleidoscope", "Overpass", "Rearview", "Open Windows", "Daydreamer", "Fault Lines", "Afterglow"
    ],
    "playlists": [
        { "id": "roadtrip", "name": "Road Trip Classics", "total": 10, "newSinceLastSync": 3 },
        { "id": "run", "name": "Morning Run", "total": 30 },
        { "id": "kitchen", "name": "Sunday Kitchen", "total": 18 },
        { "id": "focus", "name": "Deep Focus", "total": 42 },
        { "id": "summer", "name": "Summer Evenings", "total": 21 },
        { "id": "rainy", "name": "Rainy Day Acoustic", "total": 16 },
        { "id": "throwback", "name": "Throwback Thursday", "total": 35 },
        { "id": "dinner", "name": "Dinner Party", "total": 19 },
        { "id": "indie", "name": "Indie Discoveries", "total": 50, "saved": true },
        { "id": "chill", "name": "Chill Mix", "total": 40, "saved": true }
    ],
    "likedTotal": 86,
    "albums": [
        { "id": "northern", "name": "Northern Lights", "artist": "Halcyon Bay", "release": "2021-03-12", "added": "2025-03-01T00:00:00Z", "total": 11 },
        { "id": "moons", "name": "Paper Moons", "artist": "Nova Lindqvist", "release": "2019-09-27", "added": "2024-10-15T00:00:00Z", "total": 9 }
    ]
};
