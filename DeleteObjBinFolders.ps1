
<#

.SYNOPSIS
PowerShell script that recursively deletes all 'bin' and 'obj' (or any other specified) folders inside current folder

.DESCRIPTION
PowerShell script that recursively deletes all 'bin' and 'obj' (or any other specified) folders inside current folder

.PARAMETER Bin
Delete bin folders too?

.LINK
https://github.com/doblak/ps-clean

#>

[CmdletBinding()]
Param(
	[switch]$Bin
)

$CurrentPath = (Get-Location -PSProvider FileSystem).ProviderPath

# recursively get all folders matching given includes, except ignored folders
function GetFoldersToRemove([bool] $IncloudeBin)
{
    if ($IncloudeBin) {
		return Get-ChildItem .\ -include bin,obj -Recurse   | where {$_ -notmatch '_tools' -and $_ -notmatch '_build'} | foreach {$_.fullname}
	} else {
		return Get-ChildItem .\ -include obj -Recurse   | where {$_ -notmatch '_tools' -and $_ -notmatch '_build'} | foreach {$_.fullname}
	}
}

# recursively get all folders matching given includes
function GetAllFolders([bool] $IncloudeBin)
{
    if ($IncloudeBin) {
		return Get-ChildItem .\ -include bin,obj -Recurse | foreach {$_.fullname}
	} else {
		return Get-ChildItem .\ -include obj -Recurse | foreach {$_.fullname}
	}
}

$FoldersToRemove = GetFoldersToRemove $Bin
$AllFolders = GetAllFolders $Bin

# subtract arrays to calculate ignored ones
$IgnoredFolders = $AllFolders | where {$FoldersToRemove -notcontains $_} 

# remove folders and print to output
if($FoldersToRemove -ne $null)
{			
    Write-Host 
	foreach ($item in $FoldersToRemove) 
	{ 
		remove-item $item -Force -Recurse;
		Write-Host "Removed: ." -nonewline; 
		Write-Host $item.replace($CurrentPath, ""); 
	} 
}

# print ignored folders	to output
if($IgnoredFolders -ne $null)
{
    Write-Host 
	foreach ($item in $IgnoredFolders) 
	{ 
		Write-Host "Ignored: ." -nonewline; 
		Write-Host $item.replace($CurrentPath, ""); 
	} 
	
	Write-Host 
	Write-Host $IgnoredFolders.count "folders ignored" -foregroundcolor yellow
}

# print summary of the operation
Write-Host 
if($FoldersToRemove -ne $null)
{
	Write-Host $FoldersToRemove.count "folders removed" -foregroundcolor green
}
else { 	Write-Host "No folders to remove" -foregroundcolor green }	

Write-Host 

# prevent closing the window immediately
$dummy = Read-Host "Completed, press enter to continue."
