#include "Structs.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

void RealmInfo::SetWorldServerOnline(bool online)
{
	onlineStatus = (online ? 2 : 0);
	MysqlFunctions::SetWorldServerOnline(realmID, onlineStatus);
}