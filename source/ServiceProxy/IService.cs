﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceProxy
{
    public interface IService
    {
        Task<ResponseData> Process(RequestData requestData);
    }
}
