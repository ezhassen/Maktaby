; Messages this script builds at runtime, in English.
;
; Why a separate file at all: the two checkbox captions on the Finished page are created in
; [Code] with TNewCheckBox, so they never pass through the message-file machinery that
; LocalizedStrings uses for everything else. A [CustomMessages] entry is the only way to
; localize them - and it must be per language, or Arabic users see English.
;
; Referenced from the [Languages] section as the second file of the "en" entry. Anything in
; the script's own [CustomMessages] section would override this file, which is why that
; section is deliberately empty.
;
; Format follows the stock Default.isl (UTF-8, no BOM).

[CustomMessages]

LaunchMaktaby=Launch Maktaby
StartupMaktaby=Start Maktaby on Windows startup