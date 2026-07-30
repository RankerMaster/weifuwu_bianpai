# encoding: utf-8
# ----------------------------------------------------------------------
# cuckoo_search via levy flight for global optimization
#                                                          -- coded by SJ2050 in 2019
#-----------------------------------------------------------------------

import argparse
import csv
import json
import os
import re
import sys
import numpy as np
try:
    import pandas as pd
except ImportError:
    pd = None
try:
    import scipy.special as sc_special
except ImportError:
    sc_special = None
version = '1.0.0'

#   (35, 7, fit_func, nestLimit, [0, 0, 0, 0, 0, 0, 0], [4095, 4095, 4095, 4095, 4095, 4095, 4095],iter_num = 200,step_size = 10.94)
def cuckoo_search(n, m, fit_func, limit_func, lower_boundary, upper_boundary, iter_num = 150,pa = 0.30, beta = 1.5, step_size = 0.1):
    # 产生int类型35*7的巢，数值为0-4095之间
    nests = generate_nests(n, m, limit_func, lower_boundary, upper_boundary)
    #巢穴健康评估           传的是一个方法   35*7
    fitness = calc_fitness(fit_func, nests)
    #print("fitness:",fitness)
    # argmax最大值的索引
    best_nest_index = np.argmax(fitness)
    best_fitness = fitness[best_nest_index]
    best_nest = nests[best_nest_index].copy()   #最好解的坐标，用于保留最好解
    #print("BestNest:",best_nest)#1*7

    # 200 iter_num迭代次数 开始迭代！！！
    for _ in range(iter_num):
        #print ("第",_,"次迭代：")
        #return 更新后的巢穴
        nests = update_nests(fit_func, limit_func, lower_boundary, upper_boundary, nests, best_nest, fitness, step_size)
        #print("Update:",nests)
        nests = abandon_nests(nests, limit_func, lower_boundary, upper_boundary, pa)
        #print("Abandon:",nests)

        #保留本次迭代中最健康的巢穴
        fitness = calc_fitness(fit_func, nests)
        max_nest_index = np.argmax(fitness)
        max_fitness = fitness[max_nest_index]
        max_nest = nests[max_nest_index]
        #print("nests:", nests)
        #本次迭代内存限制
        #print("threshold:",threshold," iter_num:", _ ,
        #      "\n本次迭代中最健康巢穴及该巢穴的得分：",(max_nest, max_fitness))
        # 本次迭代中最健康的巢穴>历史最健康，则更新
        if (max_fitness > best_fitness):
            best_nest = max_nest.copy()
            best_fitness = max_fitness
            #print((best_nest, best_fitness))
    #       最健康的巢穴，得分
    return (best_nest, best_fitness)

def generate_nests(n, m, limit_func,lower_boundary, upper_boundary):
    """
    Generate the nests' locations
        n: Number of nests 35
        m: Number of dimensions 7
        lower_boundary: Lower boundary (example: lower_boundary = (-2, -2, -2))
        upper_boundary: Upper boundary (example: upper_boundary = (2, 2, 2))
    Output:
        generated nests' locations
    """
    lower_boundary = np.array(lower_boundary)#7个0
    upper_boundary = np.array(upper_boundary)#7个4095
    nests = np.empty((n, m),dtype=int)#35 * 7 矩阵

    for each_nest in range(n):
        nests[each_nest] = lower_boundary + np.array([np.random.rand() for _ in range(m)]) * (
                upper_boundary - lower_boundary)
        nests[each_nest] = np.asarray(nests[each_nest], dtype=int)
        nests[each_nest] = limit_func(nests[each_nest], m)#就是nestlimit
        #nestlimit返回长为7的数组 each_nest长35 所以nests为35*7
        #print("GNest:",nests[each_nest])
    #print ("nests:                ")
    #print(nests)

    return nests.astype(int)#astype转换为int，即取整

#               (fit_func, limit_func, lower_boundary, upper_boundary, nests, best_nest, fitness, step_size)
def update_nests(fit_func, limit_func, lower_boundary, upper_boundary, nests, best_nest, fitness, step_coefficient):

    lower_boundary = np.array(lower_boundary)#7个0
    upper_boundary = np.array(upper_boundary)#7个4095
    n, m = nests.shape#35*7
    # levy产生步长 也是一个35*7的矩阵
    steps = levy_flight(n, m, 1.5)
    #print("levy飞行步长steps:",steps)
    new_nests = nests.copy()

    for each_nest in range(n):
        # 系数 0.01 is to 避免levy变得过于激进10.94
        # (nest[each_nest] - best_nest)会使最好巢穴得以保留
        #           10.94               35*7
        #如果nests[each_nest]== best_nest则step_size==0
        step_size = step_coefficient * steps[each_nest] * (nests[each_nest] - best_nest)
        #('step_size1:', array([     0.  ,  -5601.28,      0.  ,      0.  , -19604.48,      0.  ,
        #                            0.  ]))
        #print("乘以因子之后的levy飞行步长step_size1:", step_size)

        #                     产生0-1的随机数
        step_direction = np.random.rand(m)#7个随机数
        #('SD:', array([0.04252948, 0.99346825, 0.11971994, 0.85011987, 0.2694836 ,
        #               0.87673199, 0.71471803]))
        #print("步长方向step_direction:",step_direction)

        #('step_size2:', array([    0.        , -5564.69383193,     0.    ,  0.    ,
        #                       -5283.08592358,     0.        ,     0.        ]))
        #print("步长方向*步长大小step_size2:",step_size * step_direction)
        new_nests[each_nest] += np.asarray(step_size * step_direction,dtype=int)
        #两个数组比较，按次序依次比较大小
        new_nests[each_nest][new_nests[each_nest] < lower_boundary] = lower_boundary[new_nests[each_nest] < lower_boundary]
        new_nests[each_nest][new_nests[each_nest] > upper_boundary] = upper_boundary[new_nests[each_nest] > upper_boundary]
        #小于阙值则改为0，大于则改为4095，保证不超越边界值
        #limit_func 对大小进行限制，超出则取消几个协议
        new_nests[each_nest] = limit_func(new_nests[each_nest],m)
        #print("up_li:", each_nest, " :", new_nests[each_nest])
        #print("LU:",new_nests[each_nest] < lower_boundary ," ",new_nests[each_nest] > upper_boundary)
        # apply boundary condtions
    new_fitness = calc_fitness(fit_func, new_nests)

    # 新点和旧点比较健康程度，更好则更新
    #print ("new_fitness.shape:    ",fitness.shape)35L
    nests[new_fitness > fitness] = new_nests[new_fitness > fitness]
    return nests

def abandon_nests(nests, limit_func, lower_boundary, upper_boundary, pa):
    lower_boundary = np.array(lower_boundary)
    upper_boundary = np.array(upper_boundary)
    n, m = nests.shape
    for each_nest in range(n):#每个蛋以0.3的概率被发现
        if (np.random.rand() < pa):#pa==0.3  被发现
            #被发现换巢
            step_size = np.random.rand() * (nests[np.random.randint(0, n)] - nests[np.random.randint(0, n)])
            nests[each_nest] += np.asarray(step_size,dtype=int)

            # 处理 小于0、大于4095
            nests[each_nest][nests[each_nest] < lower_boundary] = lower_boundary[nests[each_nest] < lower_boundary]
            nests[each_nest][nests[each_nest] > upper_boundary] = upper_boundary[nests[each_nest] > upper_boundary]
            #对总大小限制
            nests[each_nest] = limit_func(nests[each_nest],m)
    return nests

#steps = levy_flight(n, m, 1.5)
def levy_flight(n, m, beta):

    sigma_u = (sc_special.gamma(1+beta)*np.sin(np.pi*beta/2)/(sc_special.gamma((1+beta)/2)*beta*(2**((beta-1)/2))))**(1/beta)
    sigma_v = 1
    #正态分布              均值，标准差，shape35*7
    u =  np.random.normal(0, sigma_u, (n, m))
    v =  np.random.normal(0, sigma_v, (n, m))
    #随机步长
    steps = u/((np.abs(v))**(1/beta))
    #35*7
    # steps = np.asarray(steps, dtype=int)
    # print("steps:",steps)
    return steps

#               一个方法      35*7
def calc_fitness(fit_func, nests):
    n, m = nests.shape
    fitness = np.empty(n)#35L

    for each_nest in range(n):
        #                   返回的是总和
        #                   return np.sum(PArray * StationProScore)
        fitness[each_nest] = fit_func(nests[each_nest])
        #print("fitness[each_nest]:", fitness[each_nest])
    #print("fitness:",fitness)
    # print (fitness.shape)#(35L,)一个向量
    return fitness

def ten2Two(nest):
    m = nest.shape
    PArray = []  # 存储鸟巢点坐标的二进制
    PString = ''
    for j in range(m):
        PString += bin(nest[j]).replace('0b','')
    for k in PString:
        PArray.append(k)
    PArray = np.asarray(PArray, dtype=int)
    print("PArray: ",PArray)
    return PArray

#2--->10进制
def two2Ten(PArray,m):#此时的
    nest = np.empty(m,dtype=int)
    PString1 = ''
    #print("PArray",PArray)
    l = len(PArray)/m
    i =0
    for j in range(len(PArray)):
        PString1 += str(PArray[j])
        if j % l == (l - 1):
            nest[i] = int(PString1,2)#2表示输入的pstring1是二进制，要转成int
            i +=1
            PString1 = ''
    return nest#7个数字的数组
#对巢进行限制大小阈值
def nestLimit(nest,m):
    #print("nest:",nest)1*7
    PArray = []  # 存储鸟巢点坐标的二进制
    PString = ''
    for j in range(m):
        #bin：返回十进制数的二进制
        PString += (bin(nest[j]).replace('0b','')).rjust(12,'0')
        #将不足12位的01序列补足至12位， 84/7 == 12
    #print("PString:", PString)#84个01模拟84个协议
    for k in PString:
        PArray.append(k)
    #print("PArray: ", PArray)
    #PArray把PString拆分成一个长度84的数组【0.1.0.1.0.0.....】
    PArray = np.asarray(PArray, dtype=int)
    tempList = [[],[],[],[],[],[],[]]#7行，最多12列
    for i in range(len(PArray)):
        if PArray[i]:
            tempList[int(i / 12)].append(i)
    #print (tempList)
    while True:
        #psize存放协议大小，84个float parray 84个0.1
        #若1则有该协议，计算到总和中
        SumSize = np.sum(PArray * PSize)#产生的协议总大小
        #index = 0
        if SumSize > threshold:#threshold内存限制
            #以概率p选择一个第几行 返回下标
            eIndex = np.random.choice([0, 1, 2, 3, 4, 5, 6], p=eliminateP)
            # 以不同的概率选择去除1是7个维度中的哪一个
            #          __len__  返回长度
            if tempList[eIndex].__len__():
                eIndex1 = np.random.choice(tempList[eIndex])
                PArray[eIndex1] = 0#此时为坐标0-83
                tempList[eIndex].remove(eIndex1)
        else:
            break#一直执行while循环，直到满足阈值
    #该nestlimit函数限制巢穴大小不超过阈值
    return two2Ten(PArray, m)
#读协议大小
def ReadProSize():#协议大小/1000 转为float
    PData = pd.read_csv('./ReferenceData/protocol.csv')
    PSize = PData['Protocol Size']
    PSize = np.asarray(PSize, dtype=float)
    PSize = PSize / 500
    #print(PSize)
    return PSize
#读文件
def ReadEveryPro(randomFlag,trainRate,stationInfoPath):
    # 存储全部协议种类大小 84*3
    ProtocolSizeDF = pd.read_csv('./ReferenceData/protocol.csv')
    # 存储所有训练基站协议种类序列  1000*84矩阵
    StationNP = np.zeros((StationNum, 84))
    # 存储所有训练基站协议种类序列  1000*84矩阵
    ProtocolTimeND = np.zeros((StationNum, 84), dtype=float)
    #9100行数据集
    StationDF = pd.read_csv(stationInfoPath)
    #读出三列数据  9100*3
    StationDF = StationDF[['ePLC ID', 'Protocol', 'Interval Time']]
    n, m = StationDF.shape#n行m列 9100 3
    #ProtocolNum, m1 = ProtocolSizeDF.shape
    #ePLC ID  9100的第1个数据
    StationID = StationDF['ePLC ID'][0]
    #print(StationID)
    num = 0
    for i in range(n):
        if StationDF['ePLC ID'][i] != StationID:
            num += 1#再+1  则一共有多少个地址 num=999 ，一共1000个地址，则1000个样本
            StationID = StationDF['ePLC ID'][i]
        #print("run")
        #print(i)
        #ProtocolSizeDF['Protocol'] == StationDF['Protocol'][i] 84个数据一一比对，相同则1，否则false
        # ProtocolSizeDF[ProtocolSizeDF['Protocol'] == StationDF['Protocol'][i]] 第i行数据
        PIndex = ProtocolSizeDF[ProtocolSizeDF['Protocol'] == StationDF['Protocol'][i]].index.to_list()
        #
        StationNP[num][PIndex] = 1
    StationNP = np.asarray(StationNP, dtype=int)
    #print(num)#999

    #       false  randomFlag
    if not randomFlag:# 训练集              1000            0.4
        trainIndex = np.asarray(range((int)(StationNum * trainRate)),dtype= int)
        #print("trainIndex:", trainIndex)#0-399的数组
    else:
        trainIndex = np.random.randint(0, 999, StationNum * trainRate)
        #print("trainIndex:", trainIndex)

    StationID = StationDF['ePLC ID'][0]
    num = 0
    for i in range(n):
        if StationDF['ePLC ID'][i] != StationID:
            num += 1
            StationID = StationDF['ePLC ID'][i]
        PIndex = ProtocolSizeDF[ProtocolSizeDF['Protocol'] == StationDF['Protocol'][i]].index.to_list()
        if StationDF['Interval Time'][i] == 0:
            ProtocolTimeND[num][PIndex] = 15000
        else:
            ProtocolTimeND[num][PIndex] = StationDF['Interval Time'][i]
    testIndex = list(set(list(range(1000))) - set(trainIndex))#400-999
    TestNP = StationNP[testIndex]#站点样本矩阵的400-999行 测试集84列
    #print ('TestNP',TestNP)
    TrainNP = StationNP[trainIndex]#站点样本矩阵的0-399行 训练集84列
    #print ('TrainNP', TrainNP)
    TrainProTimeND = ProtocolTimeND[trainIndex]#间隔时间样本矩阵的400-999行 训练集84列
    TestProTimeND = ProtocolTimeND[testIndex]  #间隔时间样本矩阵的 0 -399行 测试集84列

    #np.savetxt()方法可以将数据保存为txt文件或者是csv文件。
    np.savetxt(TestProPath, TestNP, fmt='%d', delimiter=',')
    np.savetxt(TrainProPath, StationNP[trainIndex], fmt='%d', delimiter=',')
    np.savetxt(TestProTimePath, TestProTimeND, fmt='%d', delimiter=',')
    np.savetxt(TrainProTimePath, TrainProTimeND, fmt='%d', delimiter=',')
    #print("trainNP:",TrainNP)
    #print("TrainProTimeND", TrainProTimeND)
    return TrainProTimeND,TrainNP#返回两个训练集

trainRate = 0.8
timeScale = 0.25
lowSize = 128
highSize = 1024
iterNum = 100
stepSize = 10

randomFlag = False
StaAve = 780.414   #基站平均协议大小
StationNum = 1000
#eliminateP = np.array([0.14, 0.14, 0.14, 0.14, 0.14, 0.15, 0.15])
eliminateP = np.array([0.09, 0.12, 0.14, 0.14, 0.17, 0.17, 0.17])

TestProPath = './IntermediateData/TestPro/TestPro'+str(trainRate)+'.txt'
TrainProPath = './IntermediateData/TrainPro/TrainPro'+str(trainRate)+'.txt'
TestProTimePath = './IntermediateData/TestProTime/TestProTime'+str(trainRate)+'_'+str(timeScale)+'.txt'
TrainProTimePath = './IntermediateData/TrainProTime/TrainProTime'+str(trainRate)+'_'+str(timeScale)+'.txt'
stationInfoPath = './ReferenceData/pointtime/StationInfo_Time'+str(timeScale)+'.csv'
StationProScorePath = './ReferenceData/trainpointscore/TrainStationScore'+str(trainRate)+'.txt'
StationProScoreSumPath = './ReferenceData/trainpointscore/TrainStationScoreSum'+str(trainRate)+'.csv'

def _parse_size_to_mb(size_text):
    text = str(size_text or '').strip().upper()
    if not text:
        return 0.0
    match = re.match(r'^\s*([0-9]+(?:\.[0-9]+)?)\s*([A-Z]+)\s*$', text)
    if not match:
        return 0.0
    number = float(match.group(1))
    unit = match.group(2)
    if unit == 'TB':
        return number * 1024 * 1024
    if unit == 'GB':
        return number * 1024
    if unit == 'MB':
        return number
    if unit == 'KB':
        return number / 1024.0
    if unit == 'B':
        return number / (1024.0 * 1024.0)
    return 0.0

def _pick_field(row, candidates):
    for key in candidates:
        if key in row and str(row[key]).strip() != '':
            return str(row[key]).strip()
    return ''

def run_strategy_mode(input_csv, budget_mb, output_ids_path='', min_select=1):
    rows = []
    with open(input_csv, 'r', encoding='utf-8-sig', newline='') as f:
        reader = csv.DictReader(f)
        for row in reader:
            image_id = _pick_field(row, ['镜像ID', 'Image ID', 'image_id', 'id'])
            size_text = _pick_field(row, ['镜像大小', 'Image Size', 'size'])
            usage_count_text = _pick_field(row, ['镜像使用次数', 'Usage Count', 'usage_count'])
            usage_interval_text = _pick_field(row, ['镜像使用间隔(随机生成，没有单位)', '镜像使用间隔', 'Usage Interval', 'usage_interval'])
            if not image_id:
                continue
            size_mb = _parse_size_to_mb(size_text)
            if size_mb <= 0:
                continue
            try:
                usage_count = float(usage_count_text or 0)
            except ValueError:
                usage_count = 0.0
            try:
                usage_interval = float(usage_interval_text or 1)
            except ValueError:
                usage_interval = 1.0
            if usage_interval <= 0:
                usage_interval = 1.0
            score = (usage_count / usage_interval) / size_mb
            rows.append({
                'image_id': image_id,
                'size_mb': size_mb,
                'score': score,
            })

    rows.sort(key=lambda x: x['score'], reverse=True)
    selected = []
    used_mb = 0.0
    for item in rows:
        if used_mb + item['size_mb'] > budget_mb:
            continue
        selected.append(item['image_id'])
        used_mb += item['size_mb']

    if not selected and rows and min_select > 0:
        selected.append(rows[0]['image_id'])
        used_mb = rows[0]['size_mb']

    if output_ids_path:
        with open(output_ids_path, 'w', encoding='utf-8') as f:
            for image_id in selected:
                f.write(str(image_id) + '\n')

    result = {
        'selected_image_ids': selected,
        'used_mb': used_mb,
        'budget_mb': budget_mb,
    }
    print(json.dumps(result, ensure_ascii=False))
    return result

def _try_run_strategy_cli():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument('--mode', default='')
    parser.add_argument('--input-csv', default='image_plan_data.csv')
    parser.add_argument('--budget-mb', type=float, default=0.0)
    parser.add_argument('--output-ids', default='')
    args, _ = parser.parse_known_args()
    if str(args.mode).strip().lower() != 'strategy':
        return False

    input_csv = os.path.abspath(args.input_csv)
    if not os.path.exists(input_csv):
        print(json.dumps({'error': 'input csv not found', 'path': input_csv}, ensure_ascii=False))
        sys.exit(2)

    budget_mb = float(args.budget_mb or 0.0)
    if budget_mb <= 0:
        budget_mb = 256.0

    output_ids = str(args.output_ids or '').strip()
    if output_ids:
        output_ids = os.path.abspath(output_ids)

    run_strategy_mode(input_csv, budget_mb, output_ids_path=output_ids)
    return True

if __name__=='__main__':
    if _try_run_strategy_cli():
        sys.exit(0)

    if pd is None or sc_special is None:
        raise ImportError('legacy mode requires pandas and scipy')

    PSize = ReadProSize()#存放协议大小的数组 84个float类型
    np.set_printoptions(suppress=True)#以科学计数法形式输出
    # 存储训练集每种协议使用次数
    TrainProTimeND, TrainNP = ReadEveryPro(randomFlag, trainRate, stationInfoPath)
    #print("TrainNP.shape", TrainNP.shape)
    #print ("TrainNP:",TrainNP)
    #400*84  训练集 每个值代表有无该协议
    #print("len(TrainNP)", len(TrainNP))400
    StationProScore = []
    for i in range(len(TrainNP)):#len返回行数
        #84L          three7，2024/11/28，去掉间隔时间
        PTemp = TrainNP[i]*PSize #/ TrainProTimeND[i]#score协议大小*协议使用与否/间隔时间
        #isnan 检查一个数字是否not a number
        #是数字返回0 不是返回1
        PTemp[np.isnan(PTemp)] = 0
        StationProScore.append(PTemp)
    # PTemp=np.asarray(PTemp)
    # StationProScore = np.asarray(StationProScore)
    # print (PTemp.shape)               (84L,)
    # print (StationProScore.shape)     (400L, 84L)
    # print (PTemp)
    # print (StationProScore)
    #                               400*84
    np.savetxt(StationProScorePath, StationProScore, fmt='%f', delimiter=',')
    #score sum                  迭代器每列计算后 + 0
    StationProScoreSum =np.sum(StationProScore,0)
    #print("StationProScoreSum:",StationProScoreSum)#后续好像没用到？？？生成csv画图？？
    pd.DataFrame(list(StationProScoreSum)).to_csv(StationProScoreSumPath)

    def fit_func(nest):#一行巢穴7
        PString = ''
        PArray = []           #存储鸟巢点坐标的二进制
        #n,m = nests.shape     #nest为7维十进制整形值
        for j in range(len(nest)):
            #12*7的二进制
            PString += (bin(nest[j]).replace('0b','')).rjust(12,'0')   #strip('0b'),会把头尾的0也都删掉
        for k in PString:
            PArray.append(k)
        PArray = np.asarray(PArray, dtype=int)
        #print ("shape of PArray:", PArray.shape)#(84L,)
        #SRQTEST=np.asarray(StationProScore)
        #print ("StationProScore.shape:",SRQTEST.shape)(400L, 84L)
        #print ("PArray.shape:",PArray.shape)#(84L,)
        #print ("(PArray * StationProScore).shape:",(PArray * StationProScore).shape)(400L, 84L)
        #print ("PArray * StationProScore:",PArray * StationProScore)
        #sum函数，所有数据相加  (84L,)     (400L, 84L)
        return np.sum(PArray * StationProScore)

    BestCuckoo = []
    BestCuckooScore = []#128MB-1024MB
    #84个协议一共5514.11MB
    #threshold 门槛、阈值    128     1024     以步长16产生随机数
    kedu=128
    for threshold in range(lowSize,highSize+kedu,kedu):
        #print("Memory:",threshold)#100.105..内存限制
        #最健康的巢穴以及该巢穴的得分
        #fitness,健康适应度，或者说价值
        #把这些协议保留的价值，越大越好
        best_nest, best_fitness = cuckoo_search(35, 7, fit_func, nestLimit, [0, 0, 0, 0, 0, 0, 0], [4095, 4095, 4095, 4095, 4095, 4095, 4095],iter_num = iterNum,step_size = stepSize)
        BestCuckoo.append(best_nest)
        BestCuckooScore.append(best_fitness)
        print('threshold为%d时，最大值为:%.5f, 在(%.5f, %.5f, %.5f, %.5f, %.5f, %.5f, %.5f)处取到!' % (
        threshold,best_fitness, best_nest[0], best_nest[1], best_nest[2], best_nest[3], best_nest[4], best_nest[5], best_nest[6]))
    Best = list(zip(list(range(lowSize,highSize+kedu,kedu)),BestCuckoo,BestCuckooScore))
    Best = pd.DataFrame(Best,columns=['Memory Size','CuckooList','CuckooScore'])
    Best.to_csv('./IntermediateData/CuckooData/CuckooList_'+str(highSize)+'-'+str(lowSize)+'_'+str(iterNum)+'_'+str(stepSize)+'_'+str(trainRate)+'_'+str(timeScale)+'_1.csv')
