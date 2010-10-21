#
# Generated Makefile - do not edit!
#
# Edit the Makefile in the project folder instead (../Makefile). Each target
# has a -pre and a -post target defined where you can add customized code.
#
# This makefile implements configuration specific macros and targets.


# Environment
MKDIR=mkdir
CP=cp
CCADMIN=CCadmin
RANLIB=ranlib
CC=gcc
CCC=g++
CXX=g++
FC=
AS=as

# Macros
CND_PLATFORM=GNU-Linux-x86
CND_CONF=Debug
CND_DISTDIR=dist

# Include project Makefile
include Makefile

# Object Directory
OBJECTDIR=build/${CND_CONF}/${CND_PLATFORM}

# Object Files
OBJECTFILES= \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigInteger.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/LoginEncryption.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Logger.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Packet.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Vector3D.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/CRC32.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Functions.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/PacketBuffer.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/SwapByte.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigUnsigned.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigIntegerAlgorithms.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Timer.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigUnsignedInABase.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Client.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigIntegerUtils.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Networking.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Mutex.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/ConfigParser/Config.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Settings.o \
	${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/MysqlDatabase.o

# C Compiler Flags
CFLAGS=

# CC Compiler Flags
CCFLAGS=-lpthread
CXXFLAGS=-lpthread

# Fortran Compiler Flags
FFLAGS=

# Assembler Flags
ASFLAGS=

# Link Libraries and Options
LDLIBSOPTIONS=

# Build Targets
.build-conf: ${BUILD_SUBPROJECTS}
	${MAKE}  -f nbproject/Makefile-Debug.mk ../../../build/GNU-Linux-x86/Debug/libcommon.a

../../../build/GNU-Linux-x86/Debug/libcommon.a: ${OBJECTFILES}
	${MKDIR} -p ../../../build/GNU-Linux-x86/Debug
	${RM} ../../../build/GNU-Linux-x86/Debug/libcommon.a
	${AR} rv ../../../build/${CND_PLATFORM}/${CND_CONF}/libcommon.a ${OBJECTFILES} 
	$(RANLIB) ../../../build/GNU-Linux-x86/Debug/libcommon.a

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigInteger.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/BigInt/BigInteger.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigInteger.o ../../../src/Common/BigInt/BigInteger.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/LoginEncryption.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/LoginEncryption.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/LoginEncryption.o ../../../src/Common/LoginEncryption.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Logger.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Logger.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Logger.o ../../../src/Common/Logger.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Packet.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Packet.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Packet.o ../../../src/Common/Packet.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Vector3D.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Vector3D.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Vector3D.o ../../../src/Common/Vector3D.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/CRC32.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/CRC32.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/CRC32.o ../../../src/Common/CRC32.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Functions.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Functions.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Functions.o ../../../src/Common/Functions.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/PacketBuffer.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/PacketBuffer.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/PacketBuffer.o ../../../src/Common/PacketBuffer.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/SwapByte.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/SwapByte.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/SwapByte.o ../../../src/Common/SwapByte.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigUnsigned.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/BigInt/BigUnsigned.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigUnsigned.o ../../../src/Common/BigInt/BigUnsigned.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigIntegerAlgorithms.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/BigInt/BigIntegerAlgorithms.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigIntegerAlgorithms.o ../../../src/Common/BigInt/BigIntegerAlgorithms.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Timer.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Timer.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Timer.o ../../../src/Common/Timer.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigUnsignedInABase.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/BigInt/BigUnsignedInABase.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigUnsignedInABase.o ../../../src/Common/BigInt/BigUnsignedInABase.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Client.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Client.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Client.o ../../../src/Common/Client.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigIntegerUtils.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/BigInt/BigIntegerUtils.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/BigInt/BigIntegerUtils.o ../../../src/Common/BigInt/BigIntegerUtils.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Networking.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Networking.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Networking.o ../../../src/Common/Networking.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Mutex.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Mutex.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Mutex.o ../../../src/Common/Mutex.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/ConfigParser/Config.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/ConfigParser/Config.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/ConfigParser
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/ConfigParser/Config.o ../../../src/Common/ConfigParser/Config.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Settings.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/Settings.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/Settings.o ../../../src/Common/Settings.cpp

${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/MysqlDatabase.o: nbproject/Makefile-${CND_CONF}.mk ../../../src/Common/MysqlDatabase.cpp 
	${MKDIR} -p ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common
	${RM} $@.d
	$(COMPILE.cc) -g -Wall -D_DEBUG -MMD -MP -MF $@.d -o ${OBJECTDIR}/_ext/_DOTDOT/_DOTDOT/_DOTDOT/src/Common/MysqlDatabase.o ../../../src/Common/MysqlDatabase.cpp

# Subprojects
.build-subprojects:

# Clean Targets
.clean-conf: ${CLEAN_SUBPROJECTS}
	${RM} -r build/Debug
	${RM} ../../../build/GNU-Linux-x86/Debug/libcommon.a

# Subprojects
.clean-subprojects:

# Enable dependency checking
.dep.inc: .depcheck-impl

include .dep.inc
