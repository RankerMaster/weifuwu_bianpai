import re, warnings
import numpy as np
from .non_diff_solver import gc_solve as _fsolve
from typing import Dict, Iterable

def get_func_varnames(func):
    input_num = func.__code__.co_argcount
    params_num = func.__code__.co_kwonlyargcount
    all_varnames = func.__code__.co_varnames
    input_name = all_varnames[:input_num]
    params_name = all_varnames[input_num:input_num + params_num]
    return input_name, params_name

## Deprecated
def get_doc_varnames(func):
    comments = "".join(re.findall(r"[Α-Ωα-ωa-zA-Z0-9_.\/\*\%\[\]\(\):\n]+", func.__doc__.replace("【","[").replace("】","]").replace("(","(").replace("(","(")))
    if "[Inputs]" in comments:
        input_name = [itm.split(":",1)[0] for itm in comments.split("[Inputs]",1)[1].split("[")[0].split("\n") if itm]
    else:
        input_name = []
    if "[Params]" in comments:
        params_name = [itm.split(":",1)[0] for itm in comments.split("[Params]",1)[1].split("[")[0].split("\n") if itm]
    else:
        params_name = []
    if "[Outputs]" in comments:
        output_name = [itm.split(":",1)[0] for itm in comments.split("[Outputs]",1)[1].split("[")[0].split("\n") if itm]
    else:
        output_name = []
    return input_name, params_name, output_name

class Solver:
    def __init__(self, h=0.01, maxfev=2000, how="r-k4", n=32, x_smooth_ratio=0.001):
        self._h = h
        self._maxfev = maxfev
        self._how = how
        self._x_smooth_ratio = x_smooth_ratio
        self._n = abs(round(n))
        
        self._local_equations = []
        self._local_input_names = set()
        self._local_params_names = set()
        self._builded = False

    def set_h(self, h):
        self._h = h

    def add_equation(self, equations):
        if isinstance(equations, Dict):
            input_name, params_name = equations["input_name"], equations["params_name"]
            self._local_equations.append({
                "equation": equations["equation"],
                "input_name": input_name,
                "params_name": params_name,
                "params_map": equations["params_map"]
                })
            self._local_input_names |= set(input_name)
            self._local_params_names |= set(params_name)
            self._builded = False
            return len(equations["equation"](*[0 for _ in input_name]))
        elif isinstance(equations, Iterable):
            cnt = 0
            for equation in equations:
                cnt += self.add_equation(equation)
            return cnt
        elif callable(equations):
            input_name, params_name = get_func_varnames(equations)
            self._local_equations.append({
                "equation": equations,
                "input_name": input_name,
                "params_name": params_name,
                "params_map": {pname: pname for pname in params_name}
                })
            self._local_input_names |= set(input_name)
            self._local_params_names |= set(params_name)
            self._builded = False
            return len(equations(*[0 for _ in input_name]))

    def _nosense_approximate(self, name):
        def _inner_func(x, **kwargs):
            return 1e-3*(x - kwargs.get(f"pre_{name}", 0))/max(abs(kwargs.get(f"pre_{name}", 0)), 1)
        return _inner_func

    def _treat_n_diff(self, diff_name):
        names = []
        while diff_name.endswith("_diff"):
            names.append(diff_name)
            diff_name = diff_name[:-5]
        return names

    def build(self):
        self._diff_names = []
        for name in self._local_input_names:
            for name_ in self._treat_n_diff(name):
                if name_ not in self._diff_names:
                    self._diff_names.append(name_)
        _to_diff_names = [name[:-5] for name in self._diff_names]
        for name in _to_diff_names:
            self._local_equations.append({
                "equation": self._nosense_approximate(f"{name}_diff"),
                "input_name": [f"{name}_diff"],
                "params_name": [f"pre_{name}_diff"],
                "params_map": {f"pre_{name}_diff": f"pre_{name}_diff"}
                })
            self._local_input_names.add(f"{name}_diff")
            self._local_params_names.add(f"pre_{name}_diff")
            
        self._to_diff_names = [name for name in _to_diff_names if not name.endswith("_diff")]
        self._non_diff_names = [name for name in self._local_input_names if name not in self._diff_names and name not in self._to_diff_names]
        self._diff_count = len(self._diff_names)
        self._to_diff_count = len(self._to_diff_names)
        self._non_diff_count = len(self._non_diff_names)
        self._input_names = self._diff_names + self._to_diff_names + self._non_diff_names
        self._params_names = sorted(self._local_params_names)
        self._source_params_names = [name for name in self._params_names if name.startswith("source_")]
        self._pre_params_names = [name for name in self._params_names if name.startswith("pre_")]
        self._inner_params_names = [name for name in self._params_names if not name.startswith("source_") and not name.startswith("pre_")]
        
        def _global_eq(args, params={}):
            result = []
            for eq in self._local_equations:
                resultItm = eq["equation"](
                    *[args[self._input_names.index(arg)] for arg in eq["input_name"]],
                    **{eq["params_map"].get(k, k): v for k,v in params.items() if k in eq["params_name"]}
                    )
                if isinstance(resultItm, Iterable):
                    result.extend(list(resultItm))
                else:
                    result.append(resultItm)
            return result
        self._equation = _global_eq
        self._pre_x = np.zeros(self._non_diff_count)
        _peusdo_x = [0]*len(self._input_names)
        _peusdo_y = _global_eq(_peusdo_x)
        if len(_peusdo_x) != len(_peusdo_y):
            raise Exception(f"[SolveError] You should build the solver for a system with {len(_peusdo_x)} inputs and {len(_peusdo_y)} outputs")
        self._builded = True

    def solve(self, state=None, params={}):
        if not self._builded:
            raise Exception("[SolveError] You should build the solver via 'solver.build()' before using 'solver.solve(...)'")
        if self._how == "SomeNewMethod":
            return [], {}
        else:
            return self._rk4solve(state=state, params=params, n=self._n)
               
                
    def _rk4solve(self, state=None, params={}, n=32, silence_mode=True):
        params = {
            **{
                pre_params_name: state[self._input_names.index(pre_params_name[4:])]
                for pre_params_name in self._pre_params_names
                },
            **{
                source_params_name: state[self._diff_count+self._to_diff_count+self._source_params_names.index(source_params_name)]
                for source_params_name in self._source_params_names
                },
            **params
            }

        Y = state[self._diff_count:-self._non_diff_count]
            
        with warnings.catch_warnings():
            if silence_mode:
                warnings.simplefilter("ignore")
            k1_all = _fsolve(lambda args: self._equation(args, params), state, maxfev=self._maxfev)
            K1, Y_, X = k1_all[:self._diff_count], k1_all[self._diff_count:-self._non_diff_count], k1_all[-self._non_diff_count:]
            self._pre_x = self._x_smooth_ratio * self._pre_x + (1 - self._x_smooth_ratio)*(X - state[-self._non_diff_count:])
            X = list(X)

            Y_, X_ = [y+dy*self._h/2 for y, dy in zip(Y, K1)], [x+pre_x/2 for x, pre_x in zip(X, self._pre_x)]
            params_ = {**params, **{f"pre_{name}": val for name, val in zip(self._to_diff_names, Y_)}, **{f"source_{name}": val for name, val in zip(self._non_diff_names, X_)}}
            K2 = _fsolve(lambda args: self._equation(args, params_), state, maxfev=self._maxfev)[:self._diff_count]
            
            Y_, X_ = [y+dy*self._h/2 for y, dy in zip(Y, K2)], [x+pre_x/2 for x, pre_x in zip(X, self._pre_x)]
            params_ = {**params, **{f"pre_{name}": val for name, val in zip(self._to_diff_names, Y_)}, **{f"source_{name}": val for name, val in zip(self._non_diff_names, X_)}}
            K3 = _fsolve(lambda args: self._equation(args, params_), state, maxfev=self._maxfev)[:self._diff_count]

            Y_, X_ = [y+dy*self._h for y, dy in zip(Y, K3)], [x+pre_x for x, pre_x in zip(X, self._pre_x)]
            params_ = {**params, **{f"pre_{name}": val for name, val in zip(self._to_diff_names, Y_)}, **{f"source_{name}": val for name, val in zip(self._non_diff_names, X_)}}
            K4 = _fsolve(lambda args: self._equation(args, params_), state, maxfev=self._maxfev)[:self._diff_count]

        dY = [(k1 + 2*k2 + 2*k3 + k4)/6 for k1, k2, k3, k4 in zip(K1, K2, K3, K4)]
        Y = [y + self._h*dy for y, dy in zip(Y, dY)]
        return dY + Y + X, {name: float(val) for name, val in zip(self._input_names, dY + Y + X)}

if __name__ == "__main__":
    rksolver = Solver(0.0001)
    def source_func(z, *, source_z=0):
        return source_z - z

    rksolver.add_equation(lambda x, y_diff: y_diff - x)
    rksolver.add_equation(lambda x, z: z - x)
    rksolver.add_equation(source_func)
    
    rksolver.build()
    state = None
    for i in range(10):
        state, res = rksolver.solve(state, {"source_z": i})
        print(res)
