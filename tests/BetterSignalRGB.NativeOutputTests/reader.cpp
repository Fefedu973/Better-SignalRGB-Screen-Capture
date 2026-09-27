// SPDX-License-Identifier: GPL-2.0-or-later
// Tests include the real external reader; no copied/reimplemented Reader is used.
#include "FrameSurface/FrameSurface.h"
#include <iostream>
#include <sstream>
#ifndef HEADER_SHA256
#define HEADER_SHA256 "unspecified"
#endif

const char* status_name(room_surface::FrameStatus value)
{
    using room_surface::FrameStatus;
    switch(value)
    {
        case FrameStatus::NewFrame: return "NewFrame";
        case FrameStatus::Unchanged: return "Unchanged";
        case FrameStatus::Stale: return "Stale";
        case FrameStatus::Unavailable: return "Unavailable";
        case FrameStatus::Invalid: return "Invalid";
        case FrameStatus::Busy: return "Busy";
    }
    return "Unknown";
}

std::uint64_t hash_bytes(const std::vector<std::uint8_t>& bytes)
{
    std::uint64_t hash=14695981039346656037ULL;
    for(auto value:bytes) { hash^=value; hash*=1099511628211ULL; }
    return hash;
}
bool padding_zero(const room_surface::Frame& frame)
{
    for(unsigned y=0;y<frame.height;++y)
        for(unsigned x=frame.width*4;x<frame.stride;++x)
            if(frame.bgra[std::size_t(y)*frame.stride+x]) return false;
    return true;
}
bool uniform_pattern(const room_surface::Frame& frame)
{
    if(frame.bgra.empty()) return false;
    const unsigned char value=frame.bgra[0];
    for(unsigned y=0;y<frame.height;++y) for(unsigned x=0;x<frame.width;++x)
    {
        const auto p=std::size_t(y)*frame.stride+std::size_t(x)*4;
        if(frame.bgra[p]!=value || frame.bgra[p+1]!=(value^0x55) ||
           frame.bgra[p+2]!=255-value || frame.bgra[p+3]!=255) return false;
    }
    return padding_zero(frame);
}

int main(int argc,char** argv)
{
    if(argc!=2) return 2;
    const std::string channel=argv[1];
    room_surface::Reader reader(channel);
    room_surface::Frame frame;
    std::cout<<"READY "<<HEADER_SHA256<<std::endl;
    for(std::string line;std::getline(std::cin,line);)
    {
        std::istringstream input(line);std::string command;input>>command;
        if(command=="read")
        {
            unsigned ttl=2000,timeout=5;input>>ttl>>timeout;
            const auto status=reader.ReadLatest(frame,ttl,timeout);
            std::cout<<status_name(status)<<' '<<frame.width<<' '<<frame.height<<' '<<frame.stride<<' '
                     <<frame.sequence<<' '<<frame.generation<<' '<<frame.timestamp_ms<<' '
                     <<hash_bytes(frame.bgra)<<' '<<padding_zero(frame)<<' '<<uniform_pattern(frame)<<std::endl;
        }
        else if(command=="hold")
        {
            unsigned duration=0;input>>duration;
            if(duration>5000) return 3;
            HANDLE mutex=OpenMutexW(SYNCHRONIZE|MUTEX_MODIFY_STATE,FALSE,room_surface::detail::Name(channel,true).c_str());
            {
                room_surface::detail::Lock lock(mutex,100);
                std::cout<<(lock.acquired?"HELD":"FAILED")<<std::endl;
                if(lock.acquired) Sleep(duration);
            }
            if(mutex) CloseHandle(mutex);
            std::cout<<"RELEASED"<<std::endl;
        }
        else if(command=="abandon")
        {
            HANDLE mutex=OpenMutexW(SYNCHRONIZE|MUTEX_MODIFY_STATE,FALSE,room_surface::detail::Name(channel,true).c_str());
            room_surface::detail::Lock lock(mutex,100);
            std::cout<<(lock.acquired?"HELD":"FAILED")<<std::endl;
            // Test-process crash intentionally bypasses RAII while holding the mutex.
            ExitProcess(lock.acquired?0:4);
        }
        else if(command=="stress")
        {
            unsigned duration=0;input>>duration;
            if(duration>5000) return 3;
            unsigned fresh=0,busy=0,torn=0,other=0;
            std::cout<<"STRESS_READY"<<std::endl;
            const auto end=GetTickCount64()+duration;
            while(GetTickCount64()<end)
            {
                auto status=reader.ReadLatest(frame,2000,0);
                if(status==room_surface::FrameStatus::NewFrame) { ++fresh; if(!uniform_pattern(frame)) ++torn; }
                else if(status==room_surface::FrameStatus::Busy) ++busy;
                else if(status!=room_surface::FrameStatus::Unchanged) ++other;
                SwitchToThread();
            }
            std::cout<<"STRESS "<<fresh<<' '<<busy<<' '<<torn<<' '<<other<<std::endl;
        }
        else if(command=="close") { reader.Close();frame={};std::cout<<"CLOSED"<<std::endl; }
        else if(command=="quit") return 0;
        else { std::cout<<"UNKNOWN"<<std::endl; }
    }
    return 0;
}
