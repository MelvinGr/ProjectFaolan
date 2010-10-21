#! /bin/bash 

cd ./Common
make $1
cd ..

cd ./AgentServer
make $1
cd ..

cd ./CSPlayerAgent
make $1
cd ..

cd ./PlayerAgent
make $1
cd ..

cd ./UniverseAgent
make $1
cd ..

cd ./WorldServer
make $1
cd ..

cp ../../etc/runall.sh ../../build/GNU-Linux*/Debug/
cp ../../etc/killall.sh ../../build/GNU-Linux*/Debug/
cp ../../etc/FaolanConfig.cnf ../../build/GNU-Linux*/Debug/