#include <windows.h>
#include <iostream>
#include <string>
#include <fstream>
#include <sstream>
#include <conio.h>
#include <vector>

typedef unsigned char uint8;
typedef unsigned short uint16;
typedef unsigned int uint32;
using namespace std;

#include "structs.h"

#define VERSION "v0.1a"

namespace Buffer
{
	uint32 read(ifstream* idx);
	string read(ifstream* idx, unsigned int size, bool swap = true);
}