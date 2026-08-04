on run
    set appBundle to POSIX path of (path to me)
    set resourcesDir to appBundle & "Contents/Resources/"
    set installerScript to resourcesDir & "install.sh"
    set payloadDir to resourcesDir & "payload"

    try
        display dialog "Basic FTP Server Service will be installed as a background service that starts automatically with your Mac." & return & return & "macOS will ask for an administrator password." buttons {"Cancel", "Install"} default button "Install" cancel button "Cancel" with title "Basic FTP Server Service"
        do shell script "/bin/bash " & quoted form of installerScript & " " & quoted form of payloadDir with administrator privileges
        display dialog "Installation finished successfully." & return & return & "Next, open Terminal and use the add-user command from the release instructions to create a scanner account." buttons {"OK"} default button "OK" with title "Basic FTP Server Service" with icon note
    on error errorMessage number errorNumber
        if errorNumber is not -128 then
            display dialog "Installation failed:" & return & return & errorMessage buttons {"OK"} default button "OK" with title "Basic FTP Server Service" with icon stop
        end if
    end try
end run
