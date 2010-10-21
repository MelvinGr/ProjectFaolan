// http://www.codeproject.com/KB/files/config-file-parser.aspx
#include "Config.h"

Config::Config(string name, string parentDebugInfo) 
{
	debugInfo = parentDebugInfo + ", " + name;
}

Config::Config(string configFile, int8* envp[])
{
	while (*envp) 
	{
		string envEntry = *envp;
		size_t pos = envEntry.find('=');
		if (pos != string::npos) 
		{
			string name = envEntry.substr(0, pos);
			string value = envEntry.substr(pos + 1, string::npos);
			envSymbols[name] = value;
			//logDebug(cout << "environment symbol: '" << name << "' = '" << value << "'" << endl);
		}
		++envp;
	}

	debugInfo = configFile;
	groupStack.push_front(this);

	FILE* in = fopen(configFile.c_str(), "r");
	if (!in) 
	{
		Log.Error("Cannot open input file '%s'\n", configFile.c_str());
		return;
	}

	char buff[1024];
	while (fgets(buff, 1024, in)) 
	{
		string line = buff;
		if ((line.length() > 2) && (line[0] != '#') && (line.find(')') == string::npos)) 
		{
			string name;
			string value;
			split(line, name, value, '=');

			if (value == "(") 
			{
				//logDebug(cout << "   config: new group '" << name << "'" << endl);
				Config* newGroup = new Config(name, debugInfo);
				groupStack.front()->groups[name] = newGroup;
				groupStack.push_front(newGroup);
			} 
			else 
			{
				for (list<Config*>::reverse_iterator i = groupStack.rbegin(); i != groupStack.rend(); ++i)
				{
					(*i)->symbolExpand(value);
				}
				envSymbolExpand(value);
				//logDebug(cout << "   config: name = '" << name << "', value = '" << value << "'" << endl);
				groupStack.front()->add(name, value);
			}
		}
		if ((line.length() > 0) && (line[0] != '#') && (line.find(')') != string::npos))
		{
			//logDebug(cout << "   end of group" << endl);
			groupStack.pop_front();
		}
	}

	fclose(in);
}

Config::~Config() 
{
	for (map<string, Config*>::iterator i = groups.begin(); i != groups.end(); ++i)
	{
		delete i->second;
	}
}

void Config::add(string name, string value)
{
	symbols[name] = value;
}

void Config::split(string in, string& left, string& right, char c) 
{
	size_t pos = in.find_first_of(c);
	if(pos == string::npos)
	{
		left = in;
		trim(left);
		right = "";
	} 
	else if (pos <= 1) 
	{
		left = "";
		right = in.substr(pos+1, string::npos);
		trim(right);
	}
	else
	{
		left = in.substr(0, pos-1);
		trim(left);
		right = in.substr(pos+1, string::npos);
		trim(right);
	}
}

void Config::trim(string& s)
{
	while ((s.length() > 1) && ((s[0] == ' ') || (s[0] =='\t'))) 
	{
		s = s.substr(1, string::npos);
	}

	while ((s.length() > 1) && ((s[s.length()-1] == ' ') ||
		(s[s.length()-1] == '\t') || (s[s.length()-1] == '\n') || (s[s.length()-1] == '\r'))) 
	{
		s = s.substr(0, s.length()-1);
	}
	if ((s.length() > 1) && (s[0] == '"')) 
	{
		s = s.substr(1, string::npos);
	}
	if ((s.length() > 1) && (s[s.length()-1] == '"'))
	{
		s = s.substr(0, s.length()-1);
	}
}

void Config::symbolExpand(string& s)
{
	symbolExpand(symbols, s);
}

void Config::envSymbolExpand(string& s)
{
	symbolExpand(envSymbols, s);
}

void Config::symbolExpand(map<string, string>& symbols, string& s) 
{
	bool expanded;
	do 
	{
		expanded = false;
		for (map<string, string>::iterator i = symbols.begin(); i != symbols.end(); ++i)
		{
			string search = "%" + i->first + "%";
			string replace = i->second;
			size_t pos = s.find(search);
			if (pos != string::npos) 
			{
				expanded = true;
				s.replace(pos, search.length(), replace);
			}
		}
	} while (expanded);
}

string Config::pString(string name) 
{
	map<string, string>::iterator i = symbols.find(name);
	if (i == symbols.end()) 
	{
		Log.Error("Access of missing property '%s' (%s)\n", name.c_str(), debugInfo.c_str());
		return "";
	}

	return i->second;
}

bool Config::pBool(string name) 
{
	string val = pString(name);

	return ((val == "yes") || (val == "Yes") || (val == "YES") ||
		(val == "true") || (val == "True") || (val == "TRUE"));
}

double Config::pDouble(string name) 
{
	return atof(pString(name).c_str());
}

int Config::pInt(string name) 
{
	return atoi(pString(name).c_str());
}