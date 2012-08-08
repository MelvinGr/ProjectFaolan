/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

//#ifndef _DEBUG
#define DLLNAME "FaolanLauncher.dll"
//#else
//#define DLLNAME "FaolanLauncher.dbg.dll"
//#endif

#include <Windows.h>
#include <stdio.h>
#include <string>
#include <iostream> 

using namespace std;

#define Error(title, message) MessageBox(0, message, title, MB_ICONERROR | MB_OK)
#define Succes(title, message) MessageBox(0, message, title, MB_OK)

#define FileExists(fileName) (GetFileAttributes(fileName) != 0xFFFFFFFF)

int insertDll(HANDLE hVictim, DWORD pidVictim, string dllName)
{   
	printf("Proccess ID: %d\nDLL Path: %s\n", pidVictim, dllName.c_str());

	if(hVictim == 0)
	{
		Error("Faolan Launcher", "Couldn't find AoC Window!");
		return -1;
	}
	else printf("Found PID: %i\nFound Window Handle: %i\n", pidVictim, hVictim);

	//Load LoadLibrary function to let the victim load our dll
	HMODULE hKernel = GetModuleHandle("kernel32.dll");
	if(hKernel == 0)
	{
		Error("Faolan Launcher", "Unable to get Kernel32 handle!");
		return -2;
	}
	else printf("Found Kernel32 handle: %i\n", hKernel);

	LPTHREAD_START_ROUTINE lpfLoadLibraryA = (LPTHREAD_START_ROUTINE)GetProcAddress(hKernel, "LoadLibraryA");
	if(lpfLoadLibraryA == 0)
	{
		Error("Faolan Launcher", "Unable to get LoadLibrary function!");
		return -3;
	}
	else printf("Found LoadLibrary Function: %i\n", lpfLoadLibraryA);

	//Allocate Space for the dll name
	LPVOID pCodeAddress = (LPVOID)VirtualAllocEx(hVictim, 0, dllName.length(), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
	if(pCodeAddress == 0)
	{
		Error("Faolan Launcher", "VirtualAllocEx() failed!");
		return -4;
	}
	else printf("Allocated %i bytes for DLL at address: 0x%x\n", dllName.length(), pCodeAddress);

	//Inject dll name into victim
	if(!WriteProcessMemory(hVictim, (LPVOID)pCodeAddress, dllName.c_str(), dllName.length(), 0))
	{
		Error("Faolan Launcher", "WriteProcessMemory() failed!");
		return -5;
	}
	else printf("Injected code into victim.\n");

	//Create thread in victim with our dll code
	HANDLE hInject = CreateRemoteThread(hVictim, 0, 0, (LPTHREAD_START_ROUTINE)lpfLoadLibraryA, (LPVOID)pCodeAddress, 0, 0);
	if(hInject == 0)
	{
		Error("Faolan Launcher", "CreateRemoteThread() failed!");
		return -6;
	}
	else printf("Created thread in victim process, handle of new thread: %i\n", hInject);

	//Wait for the thread to complete, then free allocated space and shut down the thread we created
	WaitForSingleObject(hInject, INFINITE);
	VirtualFreeEx(hVictim, pCodeAddress, 0, MEM_RELEASE);
	CloseHandle(hInject);

	return 0;
}

int main(int argc, char* argv[])
{
	char aocPath[MAX_PATH] = "";
	DWORD dwaocPathSize = sizeof(aocPath);

	HKEY hKey = 0;
	DWORD dwType = REG_SZ;

	if(RegOpenKey(HKEY_LOCAL_MACHINE, "SOFTWARE\\Funcom\\Installer", &hKey) == ERROR_SUCCESS)
	{
		if(RegQueryValueEx(hKey, "LastInstalledClient", 0, &dwType, (BYTE*)aocPath, &dwaocPathSize) != ERROR_SUCCESS)
		{
			Error("Faolan Launcher", "Failed to get Age of Conan location from registry!");
			//return -1;
		}

		RegCloseKey(hKey);
	}
	else
	{
		Error("Faolan Launcher", "Failed to open registry key!");
		//return -2;
	}

	if(strlen(aocPath) == 0)
	{
		printf("Please enter the path to your Age of Conan installation: \n/> ");
		cin.get(aocPath, sizeof(aocPath));
	}

	char aocFullPath[MAX_PATH];
	sprintf_s(aocFullPath, "%s\\AgeOfConan.exe", aocPath);
	printf("Path to AoC: %s\n", aocFullPath);

	if(!FileExists(aocFullPath))
	{
		Error("Faolan Launcher", "Failed to load AoC Executable!");
		return -3;
	}

	char dllPath[MAX_PATH];
	GetCurrentDirectory(sizeof(dllPath), dllPath);
	sprintf_s(dllPath, "%s\\%s", dllPath, DLLNAME);

	if(!FileExists(dllPath))
	{
		Error("Faolan Launcher", "Failed to load FaolanLauncher(.dbg).dll!");
		return -4;
	}

	STARTUPINFO	si;
	memset(&si, 0, sizeof(si));
	si.cb = sizeof(si);

	PROCESS_INFORMATION	pi;
	if (!CreateProcess(aocFullPath, 0, 0, 0, false, 0, 0, aocPath, &si, &pi))
	{
		Error("Faolan Launcher", "Could not do: CreateProcess()!");
		return -5;
	}

	printf("Prossess Handle = %u\n", pi.hProcess);

	if(insertDll(pi.hProcess, pi.dwProcessId, dllPath) != 0)
	{
		Error("Faolan Launcher", "Injection failed!");
		return -6;
	}
	//else Succes("Faolan Launcher", "Injection successfull, Conan should be with you shortly!");

	return 0;
}