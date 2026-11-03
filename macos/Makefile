# make projet | build | test | lancer | installer
# Les produits de compilation vont hors iCloud.
DD := $(HOME)/Developer/builds/hoverguard-public
XB := xcodebuild -project Hoverguard.xcodeproj -scheme Hoverguard -derivedDataPath $(DD)
APP := $(DD)/Build/Products/Release/Hoverguard.app

projet: ; xcodegen generate
build: projet ; $(XB) -configuration Release build
test: projet ; $(XB) test
lancer: build ; open "$(APP)"
LSREG := /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
# Après installation, les copies de compilation sont retirées de Spotlight et du Launchpad : seule /Applications reste.
installer: build ; pkill -x Hoverguard || true; ditto "$(APP)" "/Applications/Hoverguard.app" && $(LSREG) -u "$(DD)/Build/Products/Release/Hoverguard.app" "$(DD)/Build/Products/Debug/Hoverguard.app" 2>/dev/null; open "/Applications/Hoverguard.app"
