Cordova Build Notes
==============
- Web Version
	- Build Web version with suffix for version on css/js refs
	- Remove/Omit Cordova script include, remove signin link
- Native Versions
	- Copy build to native apps, replacing corodova platform specific reference as required
	- Remove analytics.js reference
	- WP7 Build notes
		- Set resources (js/images etc) as Content in VS project file
		- Edit Properties/WMAppManifest.xml to include permissions:
    -iOS Build Notes
        - Uncheck Automatic Reference Counting
        - Drag www folder to project root
        - Set ExternalHosts entry (expand then add) to * to allow all remote hosts
        - Disabled transitions (slide seems to show content, then slide)
		- Replace icon with icon57x57, icon"2x with 114x114, icon 72 with 72x72
        - If app sub,mission fails due to iPad 57x57 vs 72x72 icon, try building as iphone, then set back to universal and rebuild/
Android
	- Copy AppIcon_72x72.png to res/drawable-hdpi/ic_launcher.png
	- Copy AppIcon36x36 to res/drawable-ldpi/ic_launcher.png
	- Copy AppIcon48x48 to res/drawable-mdpi/ic_launcher.png

General build notes
----------
	- Build system uses Node.js and 'jake' tool. See /misc/build/readme.md