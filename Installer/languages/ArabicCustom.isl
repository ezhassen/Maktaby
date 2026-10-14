; Messages this script builds at runtime, in Arabic.
;
; Why a separate file at all: the two checkbox captions on the Finished page are created in
; [Code] with TNewCheckBox, so they never pass through the message-file machinery that
; LocalizedStrings uses for everything else. A [CustomMessages] entry is the only way to
; localize them.
;
; Referenced from the [Languages] section as the second file of the "ar" entry. Keeping it
; out of installer.iss is deliberate: that script stays pure ASCII, so ISCC cannot misread it
; under a non-UTF-8 system code page. Wording matches the app's own strings in
; src/Maktaby/Langs/ar.xlf, which renders the app itself in Arabic ("مكتبي").
;
; Format follows the stock Arabic.isl (UTF-8, no BOM). Do not add a [LangOptions] section:
; RightToLeft, LanguageID and LanguageCodePage already come from the official Arabic.isl and
; are listed first, so this file only adds messages.

[CustomMessages]

LaunchMaktaby=تشغيل مكتبي
StartupMaktaby=بدء تشغيل مكتبي مع بدء تشغيل ويندوز