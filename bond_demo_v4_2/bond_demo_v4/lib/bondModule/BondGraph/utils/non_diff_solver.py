from scipy.optimize import root, minimize
try:
    # SciPy private API location in newer versions.
    from scipy.optimize._optimize import _linesearch_powell
except ImportError:
    # Backward compatibility for older SciPy versions.
    from scipy.optimize.optimize import _linesearch_powell
import numpy as np
import warnings

def min_solve(equation, state=None, maxfev=200, xtol=1e-15, method="Powell", direc=None, warning_ignore=False, **kwargs):
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        func = lambda x: np.linalg.norm(equation(x))**2
        params = minimize(func, state, method=method, jac='3-point',
                          tol=xtol, options=dict(maxiter=maxfev),#,direc=direc), 
                          ).x
    if not warning_ignore:
        residu = np.linalg.norm(equation(params))
        if residu > xtol:
            warnings.warn(f"(ConvergenceWarning) It remains a considerable residual {residu: .3e}.")
    return params

def belief_solve(equation, state=None, maxfev=200, xtol=1e-15, **kwargs):
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        func = lambda x: np.array(equation(x))
        params = np.real(root(func, state, method="anderson",
                               tol=xtol, options=dict(maxiter=maxfev,)).x)
    residu = np.linalg.norm(equation(params))
    if residu > xtol:
        warnings.warn(f"(ConvergenceWarning) It remains a considerable residual {residu: .3e}.")
    return params

def ga_solve(equation, state=None, maxfev=200, xtol=1e-15,
             n_population=30, n_stop=5, offset=0.5, verbose=True, **kwargs):
    innermaxfev = max(3, round(len(state)/n_population*2))
    n_iter = max(5, maxfev//innermaxfev)
    n_frist = round(n_population*0.2)
    n_mul = round(n_population*0.25)
    n_pert = round(n_population*0.3)
    n_cnt = 0
    n_input = len(state)
    state = np.array(state, dtype="float32")
    params = (1 + np.random.uniform(-offset, offset, (n_population, n_input)))
    params[:, np.abs(state)>offset] *= state[np.abs(state)>offset]
    score = np.zeros(n_population, dtype="float32")
    direc0 = np.eye(n_input)[np.random.permutation(n_input)]
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        params = np.array([min_solve(equation, param, xtol=xtol, maxfev=innermaxfev,
                                     direc=direc0[(np.arange(n_input)+pind*innermaxfev)%n_input], 
                                     warning_ignore=True) for pind, param in enumerate(params)], dtype="float32")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        score[:n_frist] = [np.sum(np.abs(equation(param))/n_input) for param in params[:n_frist]]
        score[:n_frist] = [s if not np.isnan(s) else float("inf") for s in score[:n_frist]]
    for n_ in range(n_iter):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            score[n_frist:] = [np.sum(np.abs(equation(param))/n_input) for param in params[n_frist:]]
        score = np.where(~np.isnan(score), score, float('inf'))
        params_ind = np.argsort(score)
        params = params[params_ind, :]
        score = score[params_ind]
        if verbose:
            print(f"err={score[0]: .3e}")
        if params_ind[0] == 0:
            n_cnt += 1
        else:
            n_cnt = 0
        if n_cnt >= n_stop:
            break
        params[n_frist:n_frist*2, :] = params[:n_frist]
        params_mul_f = np.random.randint(0, n_frist, n_mul)
        params_mul_m = np.random.randint(0, n_frist, n_mul)
        params_mul_m[params_mul_m == params_mul_f] += 1
        params_mul_m %= n_frist
        params[n_frist*2:n_frist*2+n_mul, :n_input//2] = params[params_mul_f, :n_input//2]
        params[n_frist*2:n_frist*2+n_mul, n_input//2:] = params[params_mul_m, n_input//2:]

        params_pert = np.random.randint(0, n_frist, n_pert)
        params[n_frist*2+n_mul:n_frist*2+n_mul+n_pert] = params[params_pert,:]
        add_part = np.random.normal(0, offset/2, n_pert)
        add_part[np.abs(state[params_pert])>offset] *= state[params_pert][np.abs(state[params_pert])>offset]
        params[n_frist*2+n_mul:n_frist*2+n_mul+n_pert, np.random.randint(0, n_input, n_pert)] += add_part
        
        params[n_frist*2+n_mul+n_pert:] = 1 + np.random.uniform(-offset, offset, (n_population-(n_frist*2+n_mul+n_pert), n_input))
        params[n_frist*2+n_mul+n_pert:, np.abs(state)>offset] *= state[np.abs(state)>offset]

        direc0 = np.eye(n_input)[np.random.permutation(n_input)]
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            params[n_frist:] = np.array([min_solve(equation, param, xtol=xtol, maxfev=innermaxfev, 
                                                   direc=direc0[(np.arange(n_input)+pind*innermaxfev)%n_input], 
                                                   warning_ignore=True) for pind, param in enumerate(params[n_frist:])], dtype="float32")
    else:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            score[n_frist:] = [np.sum(np.abs(equation(param))/n_input) for param in params[n_frist:]]
        score = np.where(~np.isnan(score), score, float('inf'))
        params_ind = np.argsort(score)
        params = params[params_ind, :]
        score = score[params_ind]
    residu = score[0]
    if residu > xtol:#offset/50:
        warnings.warn(f"(ConvergenceWarning) It remains a considerable residual {residu: .3e} after stopping at the step {n_+1}/{maxfev}.")
    return params[0]

def gc_solve(equation, state=None, xtol0=1e-10, eps=1e-10, maxfev=500, n_inner_iter=None, **kwargs):
    n_input = len(state)
    param = np.array(state) # + np.random.uniform(-eps, eps, (n_input,))
    n_inner_iter = n_inner_iter or max(5, n_input // 10)
    n_stop = round(n_input / 10 * 1.2) 
    n_cnt = 0
    xtol = xtol0
    func = lambda x: np.sum(np.array(equation(x))**2)
    gradient_coeff = np.ones(n_input, dtype="float32")
    residu = func(param)
    for n_ in range(maxfev):
        gradient_coeff /= gradient_coeff.max()
        forward_params = param + np.eye(n_input)*eps
        forward_val = [func(param_) for param_ in forward_params]
        backward_params = param - np.eye(n_input)*eps
        backward_val = [func(param_) for param_ in backward_params]
        gradient= np.array([(f_g-b_g)/eps/2 for f_g, b_g in zip(forward_val, backward_val)], dtype="float32")
        gradinds = np.argsort(np.abs(gradient)*gradient_coeff)[::-1]
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            fx = residu
            fval = fx
            x = np.array(param)
            x1 = np.array(param)
            #bigind = 0
            delta = 0.0
            direc = np.eye(n_input)[gradinds[:n_inner_iter]]
            for i in range(n_inner_iter):
                if gradient[gradinds[i]] > 0:
                    direc1 = - direc[i]
                else:
                    direc1 = direc[i]
                fx2 = fval
                try:
                    fval, x, _ = _linesearch_powell(func, x, direc1,
                                                        tol=xtol * min(1e-3/xtol, max(eps, residu/abs(gradient[gradinds[i]]))))
                
                    if (fx2 - fval) > delta:
                        delta = fx2 - fval
                        #bigind = i
                
                    direc1 = x - x1

                    if np.linalg.norm(direc1) < eps:
                        continue
                    x2 = 2*x - x1
                    x1 = x.copy()
                    fx2 = func(x2)

                    if (fx > fx2):
                        t = 2.0*(fx + fx2 - 2.0*fval)
                        temp = (fx - fval - delta)
                        t *= temp*temp
                        temp = fx - fx2
                        t -= delta*temp*temp
                        if t < 0:
                            fval, x, direc1 = _linesearch_powell(func, x, direc1,
                                                                tol=xtol * min(1e-3/xtol, max(eps, residu/abs(gradient[gradinds[i]]))))
                            #direc[bigind] = direc[-1]
                            #direc[-1] = direc1
                except:
                    pass
            param = np.array(x)
            if (residu - fval)/residu < 0.02:
                gradient_coeff[gradinds] /= np.maximum(5, 1.5 * np.abs(gradient[gradinds]/max(gradient[gradinds[round(n_inner_iter*1.5)]], eps)))
        score = func(param)
        #print(f"err={score: .3e}")
        if (residu - fval)/score < 0.002:
            n_cnt += 1
        else:
            n_cnt = 0
        if n_cnt > n_stop:
            break
        residu = score
    residu = (residu/n_input)**0.5
    if residu > 1e-5:
        warnings.warn(f"[ConvergenceWarning] It remains a considerable residual {residu: .3e} after stopping at the step {n_+1}/{maxfev}.")
    return param
