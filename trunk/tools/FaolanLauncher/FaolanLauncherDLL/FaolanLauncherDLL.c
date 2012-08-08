/*
Copyright (c) 2005-2008, Faolan Team

All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
* Neither the name of the Faolan Team nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

#include <Windows.h>

//#ifndef _DEBUG
#define DLLNAME "FaolanLauncher.dll"
//#else
//#define DLLNAME "FaolanLauncher.dbg.dll"
//#endif

#define FUNCOM_KEY_PUBLIC		"9c32cc23d559ca90fc31be72df817d0e124769e809f936bc14360ff4bed758f260a0d596584eacbbc2b88bdd410416163e11dbf62173393fbc0c6fefb2d855f1a03dec8e9f105bbad91b3437d8eb73fe2f44159597aa4053cf788d2f9d7012fb8d7c4ce3876f7d6cd5d0c31754f4cd96166708641958de54a6def5657b9f2e92"
#define FUNCOM_KEY_PRIME		"eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91cecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f"
#define FUNCOM_KEY_GENERATOR	"5"

// The Private Key here is for reference purposes only
#define CELLAO_KEY_PRIVATE		"7ad852c6494f664e8df21446285ecd6f400cf20e1d872ee96136d7744887424b"
#define CELLAO_KEY_PUBLIC		"26b5a3b4ac1177f24a2d9de44bafef477ff23ef1cb5f646919b1be26516053030b65d5afb60cef6f49de539958ba0b7922a099319b8016a8673cb27a696ae4b60fdece25ddcdad42e7f0056b87fc35687fe033b242e17e960d79806fd46c4a79cbc64f558660a50cabc1c242dace70de6af452e3433f97e30e202567f187de70"
// We keep the same prime and generator as funcom uses for simplicity sake. Although the launcher does support changing both
#define CELLAO_KEY_PRIME		"eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91cecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f"
#define CELLAO_KEY_GENERATOR	"5"

int match(unsigned char *memory, unsigned char *matchone, unsigned char *matchtwo, unsigned long max)
{
	if (IsBadReadPtr(memory, max))
		return 0;

	/*
	* Yes i know this is hackish, however since the strings are so long it is unlikely
	* that it will fail, and it is faster this way. Also the strings are checked later
	* a second time in findkeys() after this matches it. So there is no risk doing this.
	*         -- NV
	*/
	while(max--)
	{
		if ((*memory != *matchone) && (*memory != *matchtwo))
			return 0;

		matchone++;
		matchtwo++;

		memory++;
	}

	return 1;
}

unsigned long fixprotect(void *ptr, unsigned long size)
{
	MEMORY_BASIC_INFORMATION	mbi;

	VirtualQuery(ptr, &mbi, sizeof(mbi));

	switch(mbi.Protect & 0xFF)
	{
	case PAGE_EXECUTE:
	case PAGE_EXECUTE_READ:
	case PAGE_EXECUTE_READWRITE:
	case PAGE_EXECUTE_WRITECOPY:
		mbi.Protect = PAGE_EXECUTE_READWRITE;
		break;

	case PAGE_NOACCESS:
	case PAGE_READONLY:
	case PAGE_READWRITE:
	case PAGE_WRITECOPY:
		mbi.Protect = PAGE_READWRITE;
		break;
	}

	VirtualProtect(ptr, size, mbi.Protect, &mbi.Protect);

	return mbi.Protect;
}

void findkeys(unsigned char *base, unsigned long size)
{
	unsigned char	*ptr;
	unsigned long	remaining;
	unsigned long	oldprotect;

	ptr = base;
	remaining = size;

	while (remaining >= min(sizeof(FUNCOM_KEY_PUBLIC), sizeof(FUNCOM_KEY_PRIME)))
	{
		if (match(ptr, FUNCOM_KEY_PUBLIC, FUNCOM_KEY_PRIME, min(sizeof(FUNCOM_KEY_PUBLIC), sizeof(FUNCOM_KEY_PRIME))))
		{
			if (!memcmp(ptr, FUNCOM_KEY_PUBLIC, sizeof(FUNCOM_KEY_PUBLIC)))
			{
				oldprotect = fixprotect(ptr, sizeof(CELLAO_KEY_PUBLIC));

				memcpy(ptr, CELLAO_KEY_PUBLIC, sizeof(CELLAO_KEY_PUBLIC));

				VirtualProtect(ptr, sizeof(FUNCOM_KEY_PUBLIC), oldprotect, &oldprotect);

				ptr += sizeof(FUNCOM_KEY_PUBLIC) - 1;
				remaining -= sizeof(FUNCOM_KEY_PUBLIC) - 1;
			} else if (!memcmp(ptr, FUNCOM_KEY_PRIME, sizeof(FUNCOM_KEY_PRIME)))
			{
				oldprotect = fixprotect(ptr, sizeof(FUNCOM_KEY_PRIME));

				memcpy(ptr, CELLAO_KEY_PRIME, sizeof(CELLAO_KEY_PRIME));

				VirtualProtect(ptr, sizeof(FUNCOM_KEY_PRIME), oldprotect, &oldprotect);

				ptr += sizeof(FUNCOM_KEY_PRIME) - 1;
				remaining -= sizeof(FUNCOM_KEY_PRIME) - 1;
			} else
			{
				MessageBox(0, "Danger, Will Robinson.\nThis should not happen!", "Faolan Launcher", MB_ICONEXCLAMATION);

				ExitProcess(0);
			}
		}

		ptr++;
		remaining--;
	}
}

BOOL WINAPI execv_Hook(const char *cmdname, const char *const *argv)
{
	STARTUPINFO			si;
	PROCESS_INFORMATION	pi;
	HANDLE				hKernel;
	void				*dllname;
	char				*buffer;

	memset(&si, 0, sizeof(si));

	si.cb = sizeof(si);

	buffer = malloc(1024);
	strcpy_s(buffer, 1024, *argv++);

	while(*argv != NULL)
	{
		strcat_s(buffer, 1024, " ");
		strcat_s(buffer, 1024, *argv);

		argv++;
	}

	if (!CreateProcess(cmdname, buffer, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, NULL, &si, &pi))
	{
		MessageBox(0, "execv_Hook(): Failed to load start process!", "Faolan Launcher", MB_ICONEXCLAMATION);

		ExitProcess(1);
	}

	hKernel = GetModuleHandle("Kernel32.dll");

	dllname = VirtualAllocEx(pi.hProcess, NULL, sizeof(DLLNAME), MEM_RESERVE|MEM_COMMIT, PAGE_READWRITE);
	WriteProcessMemory(pi.hProcess, dllname, DLLNAME, sizeof(DLLNAME), NULL);

	if (CreateRemoteThread(pi.hProcess, NULL, 0, (LPTHREAD_START_ROUTINE)GetProcAddress(hKernel, "LoadLibraryA"), dllname, 0, NULL) == NULL)
	{
		MessageBox(0, "execv_Hook(): Failed to create remote thread.\nPlease check Anti-Virus software/etc that could be preventing this", "Faolan Launcher", MB_ICONEXCLAMATION);
	}

	CloseHandle(hKernel);

	ResumeThread(pi.hThread);

	CloseHandle(pi.hProcess);
	CloseHandle(pi.hThread);

	free(buffer);

	ExitProcess(0);

	return 0;
}

int * WINAPI spawnv_Hook(int mode, const char *cmdname, const char *const *argv)
{
	STARTUPINFO			si;
	PROCESS_INFORMATION	pi;
	HANDLE				hKernel;
	void				*dllname;
	char				*buffer;

	memset(&si, 0, sizeof(si));

	si.cb = sizeof(si);

	buffer = malloc(1024);
	strcpy_s(buffer, 1024, *argv++);

	while(*argv != NULL)
	{
		strcat_s(buffer, 1024, " ");
		strcat_s(buffer, 1024, *argv);

		argv++;
	}

	if (!CreateProcess(cmdname, buffer, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, NULL, &si, &pi))
	{
		MessageBox(0, "spawnv_Hook(): Failed to load start process!", "Faolan Launcher", MB_ICONEXCLAMATION);

		ExitProcess(1);
	}

	hKernel = GetModuleHandle("Kernel32.dll");

	dllname = VirtualAllocEx(pi.hProcess, NULL, sizeof(DLLNAME), MEM_RESERVE|MEM_COMMIT, PAGE_READWRITE);
	WriteProcessMemory(pi.hProcess, dllname, DLLNAME, sizeof(DLLNAME), NULL);

	if (CreateRemoteThread(pi.hProcess, NULL, 0, (LPTHREAD_START_ROUTINE)GetProcAddress(hKernel, "LoadLibraryA"), dllname, 0, NULL) == NULL)
	{
		MessageBox(0, "spawnv_Hook(): Failed to create remote thread.\nPlease check Anti-Virus software/etc that could be preventing this", "Faolan Launcher", MB_ICONEXCLAMATION);
	}

	CloseHandle(hKernel);

	ResumeThread(pi.hThread);

	CloseHandle(pi.hThread);

	free(buffer);

	return pi.hProcess;
}

void InstallHook()
{
	void						*hModule;
	PIMAGE_DOS_HEADER			pDosHeader;
	PIMAGE_NT_HEADERS			pNTHeaders;
	PIMAGE_IMPORT_DESCRIPTOR	pImportDescriptor;
	PIMAGE_THUNK_DATA			pThunkData;
	void						*origexecv, *origspawnv;
	char						buffer[MAX_PATH+1];
	int							i, doneclient = 0;

	hModule = LoadLibrary("msvcr71.dll");

	origexecv = GetProcAddress(hModule, "_execv");
	origspawnv = GetProcAddress(hModule, "_spawnv");

	pDosHeader = (void*)GetModuleHandle(NULL);

redo:
	if (IsBadReadPtr(pDosHeader, sizeof(IMAGE_DOS_HEADER)))
		return;

	if (pDosHeader->e_magic != IMAGE_DOS_SIGNATURE)
		return;

	pNTHeaders = (PIMAGE_NT_HEADERS)((unsigned long)pDosHeader + (unsigned long)pDosHeader->e_lfanew);

	if (IsBadReadPtr(pNTHeaders, sizeof(IMAGE_NT_HEADERS)))
		return;

	if (pNTHeaders->Signature != IMAGE_NT_SIGNATURE)
		return;

	pImportDescriptor = (PIMAGE_IMPORT_DESCRIPTOR)((unsigned long)pDosHeader + (unsigned long)(pNTHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress));

	if (pImportDescriptor == (PIMAGE_IMPORT_DESCRIPTOR)pNTHeaders)
		return;

	while(pImportDescriptor->Name)
	{
		if (!_stricmp((char*)pDosHeader + pImportDescriptor->Name, "msvcr71.dll"))
		{
			// Found msvcr71.dll Thunks
			pThunkData = (PIMAGE_THUNK_DATA)((unsigned long)pDosHeader + (unsigned long)pImportDescriptor->FirstThunk);

			while(pThunkData->u1.Function)
			{
				if((unsigned long)pThunkData->u1.Function == (unsigned long)origexecv)
				{
					unsigned long oldprotect;

					oldprotect = fixprotect(&pThunkData->u1.Function, 4);

					pThunkData->u1.Function = (unsigned long)execv_Hook;

					VirtualProtect(&pThunkData->u1.Function, 4, oldprotect, &oldprotect);
				} else if((unsigned long)pThunkData->u1.Function == (unsigned long)origspawnv)
				{
					unsigned long oldprotect;

					oldprotect = fixprotect(&pThunkData->u1.Function, 4);

					pThunkData->u1.Function = (unsigned long)spawnv_Hook;

					VirtualProtect(&pThunkData->u1.Function, 4, oldprotect, &oldprotect);
				}
				pThunkData++;
			}
			break;
		}
		pImportDescriptor++;
	}

	if (doneclient)
		return;

	doneclient = 1;

	GetModuleFileName((void*)pDosHeader, buffer, sizeof(buffer));
	buffer[sizeof(buffer)-1] = 0;

	for (i = strlen(buffer); (buffer[i] != '\\') && (i > 0); i--);

	buffer[i++] = 0;

	if (!_stricmp(&buffer[i], "Client.exe"))
	{
		pDosHeader = (void*)GetModuleHandle("GUIFunc.dll");

		goto redo;
	}
}

BOOL APIENTRY DllMain(HINSTANCE hModule, DWORD dwReason, PVOID lpReserved)
{
	switch(dwReason)
	{
	case DLL_PROCESS_ATTACH:
		{
			SYSTEM_INFO					si;
			MEMORY_BASIC_INFORMATION	mbi;
			void						*current;

			GetSystemInfo(&si);

			current = si.lpMinimumApplicationAddress;

			while (current < si.lpMaximumApplicationAddress)
			{
				VirtualQuery(current, &mbi, sizeof(mbi));

				current = mbi.BaseAddress;

				if ((mbi.State & MEM_COMMIT) && (mbi.Type & MEM_IMAGE))
				{
					findkeys(current, mbi.RegionSize);
				}

				current = (void*)((unsigned long)current + mbi.RegionSize);
			}

			InstallHook();

			break;
		}

	case DLL_PROCESS_DETACH:
		break;
	}

	return TRUE;
}